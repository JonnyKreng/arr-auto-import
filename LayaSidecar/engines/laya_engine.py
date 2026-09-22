"""Laya engine: router-based parallel decision model (the default backend)."""

from __future__ import annotations

import asyncio
import os
import re
from typing import Any, Dict, Optional

from .base import DecisionEngine, normalize_answer

KNOWN_MODELS = ("english", "multilingual", "typed-decisions")

TOKEN_ENV_VARS = ("HF_TOKEN", "HUGGING_FACE_HUB_TOKEN")

# Choice options share one head_max_len budget in laya's choice head
# (laya/common.build_sequence); beyond ~a dozen options each one is truncated past readability and
# the model can only guess among bare "option_N" labels. Instead of shrinking the option set up
# front (which can silently drop the true answer), ask the question once per chunk of CHUNK_SIZE
# options - the "no_match" option is repeated in every chunk - and run a tournament bracket:
# each chunk's top candidates advance to the next round (configurable via LAYA_TOURNAMENT_ADVANCE)
# until one song survives and is declared the winner. Confidence is either the winner's final
# round score ("last", default) or the combined evidence across every round it appeared in
# ("all").
CHUNK_SIZE = 10
TOURNAMENT_ADVANCE = 2
CONFIDENCE_MODE = "last"
CONFIDENCE_MODES = ("all", "last")
NO_MATCH = "no_match"

# Markers the worker appends to a candidate's description (ImportDecisionEngine.BuildQuestions);
# options that differ only by these describe the same track on another release (or a duplicate
# row) and are removed when merging equivalent options into one vote.
_MARKER_TARGET_RELEASE = "(target release)"
_MARKER_ALREADY_IN_LIBRARY = "(already in library)"

# "title: '<song>' | track: <n> | length: <m:ss> | release: ..." - the song title is the stable
# identity across all release renderings of a discography. Greedy so titles containing
# apostrophes ("DJ Got Us Fallin' in Love") survive; the closing quote is the one before " | track".
_TITLE_RE = re.compile(r"^title:\s*'(.*)'\s*\|\s*track")


def _song_key(text: Optional[str]) -> str:
    """Stable key for the *song* an option refers to, ignoring the release rendering.

    A discography surfaces the same song many times: per release, per disc ("track: 2-6"), and
    with " (target release)" / " (already in library)" tags. They are all the same song and must
    share one vote, so the key is the title part of the description only - not the track number,
    length or release. Variants with their own title qualifiers ("Somebody to Love (remix)...")
    keep their distinct titles, which is right: they are distinguishable song entries.
    """
    if not text:
        return ""
    m = _TITLE_RE.search(str(text))
    title = m.group(1) if m else str(text)
    return " ".join(title.lower().split())


def _prefer_key(criteria: Dict[str, Any], a: str, b: str) -> str:
    """Pick the option whose import outcome is best among equivalent tracks.

    Prefer the target release (most specific), then a listing without an existing file (the
    worker rejects+blocklists "(already in library)" candidates), then deterministically the
    first key in insertion order.
    """
    def score(key: str) -> int:
        text = str(criteria.get(key) or "")
        if _MARKER_TARGET_RELEASE in text:
            return 3
        if _MARKER_ALREADY_IN_LIBRARY not in text:
            return 2
        return 1

    return a if score(a) >= score(b) else b


def _combine_counts(probs) -> float:
    """Combine per-chunk probabilities of equivalent options into one confidence.

    Each chunk is an independent ask with its own peer-set score; the probability the song is
    right is "at least one chunk backed it". Treating the chunk draws as independent:
    ``1 - prod(1 - p_i)``. A song that a single chunk backs at 0.8 and two more at ~0.3 each
    rises to ~0.90, while a song every chunk scores at 0.3 stays ~0.66 - repeated weak support
    counts, lone moderate support does not fully count alone. Returns 0.0 for an empty sweep.
    """
    total = 1.0
    for p in probs:
        if p > 0:
            total *= 1.0 - p
    return 1.0 - total if probs else 0.0


def _sanitize_token_env() -> None:
    """Drop blank HF token env vars.

    laya forwards ``token or os.environ.get("HF_TOKEN")`` to
    ``snapshot_download``. When the environment holds an empty string (e.g.
    ``HF_TOKEN: ${HF_TOKEN:-}`` in compose.yaml), huggingface_hub builds an
    ``Authorization: Bearer `` header with no value and httpx kills startup
    with ``LocalProtocolError: Illegal header value b'Bearer '``. Removing
    blank values lets downloads proceed anonymously for public models.
    """
    for name in TOKEN_ENV_VARS:
        if name in os.environ and not os.environ[name].strip():
            os.environ.pop(name, None)


def _parse_chunk_size(raw: Optional[str]) -> int:
    """Parse LAYA_CHUNK_SIZE into a sane chunk size (>= 2), defaulting on garbage."""
    try:
        n = int((raw or "").strip())
    except (TypeError, ValueError):
        return CHUNK_SIZE
    return n if n >= 2 else CHUNK_SIZE


def _parse_advance(raw: Optional[str]) -> int:
    """Parse LAYA_TOURNAMENT_ADVANCE into a sane advance count (>= 1), defaulting on garbage."""
    try:
        n = int((raw or "").strip())
    except (TypeError, ValueError):
        return TOURNAMENT_ADVANCE
    return n if n >= 1 else TOURNAMENT_ADVANCE


def _parse_confidence_mode(raw: Optional[str]) -> str:
    """Parse LAYA_TOURNAMENT_CONFIDENCE into "all" or "last", defaulting on garbage."""
    mode = (raw or "").strip().lower()
    return mode if mode in CONFIDENCE_MODES else CONFIDENCE_MODE


def _needs_chunking(questions: Dict[str, Any], chunk_size: int) -> bool:
    """True when any choice question has more real options than one chunk can hold."""
    for qdef in questions.values():
        if not isinstance(qdef, dict) or qdef.get("type") != "choice":
            continue
        criteria = qdef.get("criteria")
        if not isinstance(criteria, dict):
            continue
        real = sum(1 for key in criteria if key != NO_MATCH)
        if real > chunk_size:
            return True
    return False


def _usage_tokens(result: Dict[str, Any]) -> int:
    usage = result.get("usage") or {}
    return int(usage.get("input_tokens") or 0)


def _advance_from_rankings(rankings: list[list[str]], advance: int) -> list[str]:
    """Collect the top ``advance`` songs of each chunk, deduplicated across chunks."""
    out: list[str] = []
    for chunk in rankings:
        for song in chunk[:advance]:
            if song not in out:
                out.append(song)
    return out


def _decide_choice_tournament(agent: Any, state: Any, qid: str, qdef: Dict[str, Any],
                              chunk_size: int, advance: int,
                              confidence_mode: str) -> tuple[Dict[str, Any], Dict[str, Any]]:
    """Score one choice question as a tournament bracket.

    Candidate options share one head_max_len budget per model call, so beyond CHUNK_SIZE options
    they are truncated past readability. Instead of presenting them all at once, the candidates
    are grouped by song first (duplicate release renderings of the same song are one entrant and
    can never split a chunk's vote) and then knocked out round by round:

    * round 0 presents every song exactly once, split into chunks of ``chunk_size``; the
      "no_match" option is repeated in every chunk so each batch can abstain;
    * each chunk advances its top ``advance`` songs (default 2) to the next round and the
      bracket repeats until one song (the champion) survives, or the field is small enough for a
      single deciding round;
    * the deciding round is a straight head-to-head: the champion is the top song of that final
      chunk and "no_match" only wins if it out-scores the champion right there. Abstentions
      accumulated across earlier chunks do not stack into a win, so "no_match" is eliminated the
      moment a real song beats it - it cannot ride 1 - prod(1 - p) across ten chunks past a
      champion that beat it outright.

    A song that reaches the final round and wins is not necessarily backed the hardest by raw
    probability, so confidence is explicitly two modes (LAYA_TOURNAMENT_CONFIDENCE). The mode
    rescales the reported number only; it never flips the winner:

    * "all" (default): the evidence accumulated across every round the winner appeared in,
      combined as independent asks (``1 - prod(1 - p)``) - repeated support counts, lone
      moderate support does not fully count;
    * "last": only the deciding round's score - the bare "how sure was the final head-to-head".

    Because bracket rounds shrink the peer set each time, probabilities are not comparable across
    rounds (laya buckets its choice temperature by peer-set size); "last" trusts the final round
    alone, "all" averages evidence across different peer sets.
    """
    criteria = dict(qdef.get("criteria") or {})
    no_match_text = criteria.pop(NO_MATCH, None)

    keys = list(criteria)

    # One entrant per *song*: duplicate renderings collapse before any model call.
    members: Dict[str, list[str]] = {}
    for key in keys:
        members.setdefault(_song_key(criteria[key]), []).append(key)
    rep: Dict[str, str] = {}
    for song, song_keys in members.items():
        best = song_keys[0]
        for candidate in song_keys[1:]:
            best = _prefer_key(criteria, best, candidate)
        rep[song] = best

    pool = list(members)
    rounds: list[Dict[str, Any]] = []
    evidence: list[Dict[str, float]] = []
    no_match_chunks: list[float] = []
    usage_tokens = 0
    option_prob: Dict[str, float] = {}

    def run_round(songs: list[str]) -> tuple[list[Any], Dict[str, float], list[list[str]]]:
        """Score one bracket round: enter the songs (by representative), evaluate one chunk per
        chunk_size songs, return (answers, per-song round scores, per-chunk song rankings)."""
        nonlocal usage_tokens, option_prob
        entered = [rep[s] for s in songs]
        chunks = [entered[i:i + chunk_size] for i in range(0, len(entered), chunk_size)]
        runs = []
        round_ev: Dict[str, float] = {}
        rankings: list[list[str]] = []
        for ci, chunk_keys in enumerate(chunks):
            chunk_criteria = {k: criteria[k] for k in chunk_keys}
            if no_match_text is not None:
                chunk_criteria[NO_MATCH] = no_match_text
            chunk_q = {"type": "choice", "instructions": qdef.get("instructions", ""),
                       "criteria": chunk_criteria}
            round_qid = f"{qid}#r{len(rounds)}_c{ci}"
            result = agent.system_one(state, {round_qid: chunk_q})
            usage_tokens += _usage_tokens(result)
            run_answer = result["answers"][round_qid]
            runs.append(run_answer)
            prob = {k: float(p) for k, p in (run_answer.get("probabilities") or {}).items()}
            chunk_songs: Dict[str, float] = {}
            for key, p in prob.items():
                if key == NO_MATCH:
                    no_match_chunks.append(p)
                    continue
                option_prob[key] = p
                song = _song_key(criteria[key])
                round_ev[song] = max(round_ev.get(song, 0.0), p)
                chunk_songs[song] = max(chunk_songs.get(song, 0.0), p)
            ranked = sorted(chunk_songs.items(), key=lambda kv: -kv[1])
            nm_p = prob.get(NO_MATCH) if no_match_text is not None else None
            terms = [f"{song}={p:.3f}" for song, p in ranked]
            if nm_p is not None:
                terms.append(f"no_match={nm_p:.3f}")
            print(f"[laya] compare {round_qid}: " + ", ".join(terms))
            rankings.append([s for s, _ in ranked])
        return runs, round_ev, rankings

    # Bracket rounds while the field is too big for one chunk.
    while len(pool) > chunk_size:
        runs, round_ev, rankings = run_round(pool)
        advancers = _advance_from_rankings(rankings, advance)
        # Guarantee the field shrinks: if the requested advance can't reduce it, fall back to
        # top-1 per chunk (always smaller than the field since there are >= 2 chunks).
        if len(advancers) >= len(pool) and advance > 1:
            advancers = _advance_from_rankings(rankings, 1)
        print(f"[laya] round {len(rounds)} advancers: " + ", ".join(advancers))
        rounds.append({"round": len(rounds), "chunks": runs,
                       "advancers": [rep[s] for s in advancers]})
        evidence.append(round_ev)
        pool = advancers
        if not pool:
            break

    # Deciding round: the surviving songs (<= chunk_size) face off in one final chunk.
    if pool:
        runs, round_ev, rankings = run_round(pool)
        rounds.append({"round": len(rounds), "chunks": runs,
                       "advancers": [rep[s] for s in (rankings[0] if rankings else [])]})
        evidence.append(round_ev)
        champion = rankings[0][0] if rankings and rankings[0] else None
    else:
        champion = None

    # The deciding round settles the winner: no_match only wins by out-scoring the champion in
    # that straight head-to-head. Abstentions accumulated in earlier chunks/stages MUST NOT stack
    # into a win (1 - prod(1 - p)) across many chunks would dwarf any one song's evidence and
    # make no_match always win. The confidence mode then only rescales the reported number.
    champion_scores = [ev.get(champion) for ev in evidence if champion in ev]
    champion_deciding = champion_scores[-1] if champion_scores else 0.0
    no_match_deciding = no_match_chunks[-1] if no_match_chunks else 0.0
    no_match_wins = champion is None or no_match_deciding > champion_deciding

    if no_match_wins:
        choice, confidence, confidence_from = NO_MATCH, 0.0, "no_match"
        scores = no_match_chunks
        verdict = (f"no_match {no_match_deciding:.3f} beats champion "
                   f"{champion} {champion_deciding:.3f}") if champion is not None else \
            "no_match (empty field)"
    else:
        choice, confidence, confidence_from = rep[champion], 0.0, (
            "final_round" if confidence_mode == "last" else "all_rounds")
        scores = champion_scores
        verdict = (f"{champion} {champion_deciding:.3f} beats no_match "
                   f"{no_match_deciding:.3f}")
    confidence = (scores[-1] if scores else 0.0) if confidence_mode == "last" \
        else _combine_counts(scores)
    print(f"[laya] deciding round {len(rounds) - 1}: {verdict} -> {choice}")

    votes = []
    for song, song_keys in members.items():
        scores = [ev.get(song) for ev in evidence if song in ev]
        song_prob = (scores[-1] if scores else 0.0) if confidence_mode == "last" \
            else _combine_counts(scores)
        votes.append({"text": song, "representative": rep[song],
                      "probability": round(song_prob, 4),
                      "members": list(song_keys), "rounds": len(scores)})
    votes.sort(key=lambda v: (-v["probability"], v["text"]))

    resolved_answer = {
        "type": "choice",
        "choice": choice,
        "probabilities": {k: round(p, 4) for k, p in option_prob.items()},
        "confidence": round(float(confidence), 4),
        "action": {"act_probability": 1.0},
    }

    no_match_value = (no_match_chunks[-1] if no_match_chunks else 0.0) if confidence_mode == \
        "last" else _combine_counts(no_match_chunks)

    return resolved_answer, {
        "rerun": len(keys) > chunk_size,
        "rounds": rounds,
        "advance": advance,
        "confidence_mode": confidence_mode,
        "winners": rounds[0]["advancers"] if rounds else [],
        "votes": votes,
        "winner": champion if champion is not None else NO_MATCH,
        "winner_option": choice,
        "no_match_probability": round(no_match_value, 4),
        "confidence_from": confidence_from,
        "usage_tokens": usage_tokens,
    }


def _decide_chunked(agent: Any, state: Any, questions: Dict[str, Any],
                    chunk_size: int, advance: int,
                    confidence_mode: str) -> Dict[str, Any]:
    out: Dict[str, Any] = {}
    segments: Dict[str, Any] = {}
    usage_tokens = 0

    for qid, qdef in questions.items():
        if not isinstance(qdef, dict) or qdef.get("type") != "choice":
            result = agent.system_one(state, {qid: qdef})
            out[qid] = result["answers"][qid]
            usage_tokens += _usage_tokens(result)
            continue

        answer, meta = _decide_choice_tournament(agent, state, qid, qdef, chunk_size,
                                                 advance, confidence_mode)

        out[qid] = answer
        segments[qid] = meta
        usage_tokens += meta["usage_tokens"]

    return {
        "answers": out,
        "usage": {"input_tokens": usage_tokens, "output_tokens": 0},
        "segments": segments,
    }


class LayaEngine(DecisionEngine):
    name = "laya"

    def __init__(self, env: Optional[Dict[str, str]] = None):
        super().__init__(env)
        self._model = self._env.get("LAYA_MODEL", "typed-decisions").strip() or "typed-decisions"
        device = self._env.get("LAYA_DEVICE", "").strip()
        # "":None lets Router auto-detect cuda/mps/cpu. Never pass the literal string
        # "auto": Agent() does torch.device(device) and "auto" is not a device name.
        self._device = None if device in ("", "auto") else device
        self._chunk_size = _parse_chunk_size(self._env.get("LAYA_CHUNK_SIZE"))
        self._advance = _parse_advance(self._env.get("LAYA_TOURNAMENT_ADVANCE"))
        self._confidence_mode = _parse_confidence_mode(
            self._env.get("LAYA_TOURNAMENT_CONFIDENCE"))
        preload = self._env.get("LAYA_PRELOAD", "").strip()
        if preload:
            self._preload = [name.strip() for name in preload.split(",") if name.strip()]
        elif self._model == "auto":
            self._preload = ["english", "multilingual"]
        else:
            self._preload = [self._model]
        self._router = None
        self._model_label = f"{self._model} (laya)"
        self._backend_label = "auto" if self._device is None else self._device

    async def start(self) -> None:
        self._router = await asyncio.to_thread(self._load_router)
        self._ready = True

    def _load_router(self) -> Any:
        from laya import Router

        _sanitize_token_env()

        if self._model != "auto" and self._model not in KNOWN_MODELS:
            raise ValueError(
                f"LAYA_MODEL={self._model!r} is not known; choose from {KNOWN_MODELS} or 'auto'"
            )
        router = Router(device=self._device)
        router.preload(self._preload)
        return router

    def decide(self, state: Any, questions: Dict[str, Any]) -> Dict[str, Any]:
        if self._router is None or not self._ready:
            raise RuntimeError("laya engine is not initialised")

        decision = self._router.route(
            state, questions, model=None if self._model == "auto" else self._model
        )
        agent = self._router.load(decision["model"])

        if _needs_chunking(questions, self._chunk_size):
            result = _decide_chunked(agent, state, questions, self._chunk_size,
                                     self._advance, self._confidence_mode)
        else:
            result = agent.system_one(state, questions)
        result["routing"] = dict(decision)

        return {
            "model": self._model_label,
            "answers": {qid: normalize_answer(dict(ans)) for qid, ans in result["answers"].items()},
            "usage": result.get("usage"),
            "routing": result.get("routing"),
            "segments": result.get("segments"),
        }