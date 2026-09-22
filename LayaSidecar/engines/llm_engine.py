"""LLM engine: delegates the typed questions to a hosted OpenAI-compatible chat endpoint.

Serves the same /decide contract as the laya backend (choice + noul answers with a
``confidence`` the worker gates on), but each question is a single chat-completions call
instead of a local checkpoint. Configuration is env-driven:

* LLM_URL      - base URL of an OpenAI-compatible server, e.g. http://192.168.2.2:8686
                  (the engine appends /v1/chat/completions).
* LLM_API_KEY  - optional bearer token sent as ``Authorization: Bearer ...``.
* LLM_MODEL    - model id reported to the server.

At startup the engine probes ``GET /api/version``: when the host is an Ollama server it
switches to Ollama's native ``/api/chat`` with ``think=false`` + ``format=json``, because
Ollama's OpenAI-compat route does not disable reasoning - a thinking model burns the token
budget in ``reasoning`` and returns empty ``content``. Any other host keeps the generic
``/v1/chat/completions`` contract.

The model is asked for a single JSON object per question; output is parsed
tolerantly (fenced/trimmed/re-flowed) and normalized through ``normalize_answer``,
so the worker's confidence gating behaves identically to laya.
"""

from __future__ import annotations

import asyncio
import json
import re
import time
from typing import Any, Dict, Optional

import httpx

from .base import DecisionEngine, normalize_answer

DEFAULT_TIMEOUT_S = 600.0
DETECT_TIMEOUT_S = 15.0

SYSTEM_PROMPT = (
    "You are the import decision engine of a music library (Lidarr). A downloaded music "
    "file must be matched against the album the library wants to import it into. You answer "
    "focused questions about that match. Always respond with a single JSON object and "
    "nothing else - no markdown, no prose."
)

_JSON_OPEN_RE = re.compile(r"\{")


def _json_from_text(text: str) -> Any:
    """Extract the first balanced top-level JSON object from model output."""
    for begin in _JSON_OPEN_RE.finditer(text):
        depth = 0
        for i in range(begin.start(), len(text)):
            ch = text[i]
            if ch == "{" or ch == "[":
                depth += 1
            elif ch == "}" or ch == "]":
                depth -= 1
                if depth == 0:
                    try:
                        return json.loads(text[begin.start():i + 1])
                    except json.JSONDecodeError:
                        break
    return None


def _clamp(value: Any, lo: float = 0.0, hi: float = 1.0) -> float:
    try:
        return max(lo, min(hi, float(value)))
    except (TypeError, ValueError):
        return 0.0


def _criteria_lines(criteria: Any) -> str:
    if not isinstance(criteria, dict):
        return ""
    return "\n".join(f"- {key}: {text}" for key, text in criteria.items() if text)


def _chat_messages(question_type: str, state_text: str, qdef: Dict[str, Any]) -> list[Dict[str, str]]:
    instructions = str(qdef.get("instructions") or "")
    criteria = qdef.get("criteria")

    if question_type == "noul":
        false_text = ""
        true_text = ""
        if isinstance(criteria, dict):
            false_text = str(criteria.get("false") or "")
            true_text = str(criteria.get("true") or "")
        user = "\n\n".join(
            part for part in (
                "Download context (JSON):\n" + state_text,
                f"Include this download into the target album.\nQuestion:\n{instructions}",
                "Interpretation:\n"
                + ("- false: " + false_text if false_text else "")
                + ("\n- true: " + true_text if true_text else ""),
                (
                    'Return JSON: {"noul": <0..1>, "confidence": <0..1>}. '
                    '"noul" is the probability that the "true" statement applies '
                    '(0 = definitely false, 1 = definitely true). '
                    '"confidence" is how sure you are of this answer (0..1).'
                ),
            ) if part
        )
    else:
        options = _criteria_lines(criteria)
        user = "\n\n".join(
            part for part in (
                "Download context (JSON):\n" + state_text,
                f"Include this download into the target album.\nQuestion:\n{instructions}",
                "Options (key: candidate). Answer with the KEY of the option you choose:\n" + options,
                (
                    'Return JSON: {"choice": "<key>", "confidence": <0..1>}. '
                    '"confidence" is how sure you are (1.0 = certain). If none of the options '
                    "fits the download, choose \"no_match\". Never invent a key - use one of "
                    "the keys listed above."
                ),
            ) if part
        )

    return [
        {"role": "system", "content": SYSTEM_PROMPT},
        {"role": "user", "content": user},
    ]


def _prompt_size(messages: list[Dict[str, str]]) -> int:
    return sum(len(m.get("content") or "") for m in messages)


class LlmEngine(DecisionEngine):
    name = "llm"

    def __init__(self, env: Optional[Dict[str, str]] = None):
        super().__init__(env)
        self._base_url = self._env.get("LLM_URL", "").strip().rstrip("/")
        self._api_key = self._env.get("LLM_API_KEY", "").strip()
        self._model = self._env.get("LLM_MODEL", "").strip()
        self._model_label = f"{self._model or self._base_url} (llm)"
        self._backend_label = self._base_url or "unconfigured"
        self._client: Optional[httpx.Client] = None
        self._ollama = False

    @property
    def ready(self) -> bool:
        # Config-only check: no weights to download, so readiness is immediate once the
        # endpoint configuration validates. Unreachable upstream surfaces per-decision
        # failures (worker leaves those to a human) rather than a blocked boot.
        return self._ready

    async def start(self) -> None:
        if not self._base_url:
            raise ValueError("LLM_URL is required for the llm engine")
        if not self._model:
            raise ValueError("LLM_MODEL is required for the llm engine")

        headers = {"Content-Type": "application/json"}
        if self._api_key:
            headers["Authorization"] = f"Bearer {self._api_key}"

        self._client = httpx.Client(
            headers=headers,
            timeout=DEFAULT_TIMEOUT_S,
        )
        self._ollama = self._is_ollama()
        self._endpoint = (
            f"{self._base_url}/api/chat" if self._ollama
            else f"{self._base_url}/v1/chat/completions"
        )

        # Load the model before flipping /ready: self-hosted backends (Ollama, llama.cpp,
        # vLLM) pull weights and warm up on the first request, which can take minutes on a
        # cold boot. The worker waits on /ready (Lidarr:SidecarReadyTimeoutSeconds), so the
        # first /decide is never the one paying the load cost.
        await asyncio.to_thread(self._warm_up)
        self._ready = True

    def _is_ollama(self) -> bool:
        """Detect an Ollama host so a thinking model's reasoning can be disabled natively."""
        try:
            response = self._client.get(f"{self._base_url}/api/version", timeout=DETECT_TIMEOUT_S)
            response.raise_for_status()
            return isinstance(response.json(), dict) and "version" in response.json()
        except (httpx.HTTPError, ValueError):
            return False

    def _warm_up(self) -> None:
        if self._ollama:
            payload = {
                "model": self._model,
                "messages": [{"role": "user", "content": "Say ok."}],
                "stream": False,
                "think": False,
                "format": "json",
                "options": {"temperature": 0, "num_predict": 8},
            }
        else:
            payload = {
                "model": self._model,
                "messages": [{"role": "user", "content": "Say ok."}],
                "temperature": 0,
                "max_tokens": 8,
            }
        try:
            response = self._client.post(self._endpoint, json=payload)
            response.raise_for_status()
        except httpx.HTTPError as ex:
            raise RuntimeError(
                f"LLM endpoint {self._endpoint} is not reachable: {ex}"
            ) from ex

    async def stop(self) -> None:
        if self._client is not None:
            self._client.close()
            self._client = None
        await super().stop()

    def decide(self, state: Any, questions: Dict[str, Any]) -> Dict[str, Any]:
        if self._client is None or not self._ready:
            raise RuntimeError("llm engine is not initialised")

        state_text = json.dumps(state, sort_keys=True, ensure_ascii=True) if state is not None else "null"

        answers: Dict[str, Any] = {}
        segments: Dict[str, Any] = {}
        usage_tokens = 0

        for qid, qdef in questions.items():
            qdef = qdef if isinstance(qdef, dict) else {"type": "choice", "instructions": str(qdef)}
            question_type = str(qdef.get("type") or "choice")
            messages = _chat_messages(question_type, state_text, qdef)
            usage_tokens += _prompt_size(messages)

            if self._ollama:
                payload = {
                    "model": self._model,
                    "messages": messages,
                    "stream": False,
                    "think": False,
                    "format": "json",
                    "options": {"temperature": 0, "num_predict": 200},
                }
            else:
                payload = {
                    "model": self._model,
                    "messages": messages,
                    "temperature": 0,
                    "max_tokens": 200,
                }
                if question_type == "choice":
                    payload["response_format"] = {"type": "json_object"}

            start = time.perf_counter()
            try:
                response = self._client.post(self._endpoint, json=payload)
                response.raise_for_status()
                body = response.json()
            except httpx.HTTPError as ex:
                raise RuntimeError(f"LLM request to {self._endpoint} failed: {ex}") from ex

            latency_ms = round((time.perf_counter() - start) * 1000, 2)
            message = self._message_of(body)
            raw = str(message.get("content") or message.get("reasoning") or "")
            parsed = _json_from_text(raw) if raw else None

            if not isinstance(parsed, dict):
                answer = {"type": question_type, "choice": None, "confidence": 0.0,
                          "parse_error": "no JSON object in model output"}
            elif question_type == "noul":
                answer = {
                    "type": "noul",
                    "noul": round(_clamp(parsed.get("noul", 0.5)), 4),
                    "confidence": round(_clamp(parsed.get("confidence", 0.0)), 4),
                }
            else:
                choice = str(parsed.get("choice") or "")
                confidence = _clamp(parsed.get("confidence", 0.0))
                answer = {
                    "type": "choice",
                    "choice": choice or None,
                    "probabilities": {choice: round(confidence, 4)} if choice else None,
                    "confidence": round(confidence, 4),
                    "action": {"act_probability": 1.0},
                }

            answers[qid] = normalize_answer(answer)
            segments[qid] = {
                "type": question_type,
                "model": self._model,
                "latency_ms": latency_ms,
                "prompt_chars": len(messages[1]["content"]),
            }

        return {
            "model": self._model_label,
            "answers": answers,
            "usage": {"input_tokens": usage_tokens, "output_tokens": 0},
            "segments": segments,
        }

    @staticmethod
    def _message_of(body: Any) -> Dict[str, Any]:
        """Extract the assistant message dict from an OpenAI- or Ollama-shaped response."""
        if not isinstance(body, dict):
            return {}
        message = body.get("message")
        if isinstance(message, dict):
            return message
        choices = body.get("choices")
        if isinstance(choices, list) and choices and isinstance(choices[0], dict):
            message = choices[0].get("message")
            if isinstance(message, dict):
                return message
        return {}