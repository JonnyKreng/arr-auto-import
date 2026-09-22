import os
import time
from contextlib import asynccontextmanager
from typing import Any, Dict

from fastapi import FastAPI
from pydantic import BaseModel

try:
    import von
except ImportError as e:  # pragma: no cover
    raise SystemExit("von is not installed; run: pip install -r requirements.txt") from e

MODEL = os.environ.get("VON_MODEL", "von-latest")
BACKEND = os.environ.get("VON_BACKEND", "von-1.0")

class Question(BaseModel):
    type: str
    instructions: str
    criteria: Dict[str, Any] | None = None


class DecideRequest(BaseModel):
    state: Any = None
    questions: Dict[str, Question]


class DecideResponse(BaseModel):
    model: str
    answers: Dict[str, Any]
    routing: Dict[str, Any] | None = None
    usage: Dict[str, Any] | None = None
    latency_ms: float | None = None


_ready = False


def _answer_dict(answer: Any) -> Dict[str, Any]:
    """Normalize a Von answer into the plain JSON the worker expects.

    Von's ChoiceAnswer/ScoreAnswer carry `choice`/`score` + `confidence`; its NoulAnswer only
    carries `noul` (no confidence). The worker gates the reject question on `confidence`, so we
    synthesize it with the same top1 - top2 margin Von uses for its categorical answers
    (binary margin = |2 * noul - 1|). Extra Laya fields the worker may read ({choice, noul,
    score, probabilities, confidence}) all survive the normalization.
    """
    data = answer.model_dump(exclude_none=True)
    if data.get("type") == "noul" and "confidence" not in data:
        noul = float(data.get("noul", 0.5))
        data["confidence"] = round(abs(2.0 * noul - 1.0), 4)
    return data


@asynccontextmanager
async def lifespan(app: FastAPI):
    global _ready
    if not os.environ.get("VON_DEVICE", "").strip():
        os.environ.pop("VON_DEVICE", None)
    von.set_backend(BACKEND)  # device comes from VON_DEVICE env (auto-read by VonEngine)
    # Warm-up: Von loads weights lazily on the first decision; force the download + load now
    # (first boot downloads ~1.5 GB from the HF Hub) so /health stays down until it is ready.
    von.system_one({"warmup": True}, {"probe": {"type": "noul", "instructions": "Is the system ready?"}},
                   model=MODEL)
    _ready = True
    yield
    _ready = False


app = FastAPI(title="Arr Import Solver — Von sidecar", lifespan=lifespan)


@app.get("/health")
def health():
    return {"ok": _ready, "model": MODEL, "backend": BACKEND}


@app.post("/decide", response_model=DecideResponse)
def decide(req: DecideRequest):
    if not _ready:
        raise RuntimeError("sidecar not initialised")

    questions = {
        qid: q.model_dump(exclude_none=True, exclude_unset=True)
        for qid, q in req.questions.items()
    }

    start = time.perf_counter()
    result = von.system_one(req.state, questions, model=MODEL)
    latency_ms = round((time.perf_counter() - start) * 1000, 2)

    return DecideResponse(
        model=str(result.model or MODEL),
        answers={qid: _answer_dict(ans) for qid, ans in result.answers.items()},
        routing=None,
        usage=result.usage.model_dump(exclude_none=True) if result.usage is not None else None,
        latency_ms=latency_ms,
    )