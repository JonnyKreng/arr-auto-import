import time
from contextlib import asynccontextmanager
from typing import Any, Dict

from fastapi import FastAPI
from pydantic import BaseModel

from engines import DecisionEngine, engine_from_env


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
    segments: Dict[str, Any] | None = None
    latency_ms: float | None = None


_engine: DecisionEngine | None = None
_ready = False


@asynccontextmanager
async def lifespan(app: FastAPI):
    global _engine, _ready
    _engine = engine_from_env()
    await _engine.start()
    _ready = _engine.ready
    try:
        yield
    finally:
        _ready = False
        await _engine.stop()


app = FastAPI(title="Arr Import Solver - decision sidecar", lifespan=lifespan)


@app.get("/health")
def health():
    return {
        "ok": _ready,
        "model": _engine.model if _engine else "unconfigured",
        "backend": _engine.backend if _engine else "unconfigured",
    }


@app.get("/ready")
def ready():
    return {"ready": _ready}


@app.post("/decide", response_model=DecideResponse)
def decide(req: DecideRequest):
    if not _ready or _engine is None:
        raise RuntimeError("sidecar not initialised")

    questions = {
        qid: q.model_dump(exclude_none=True, exclude_unset=True)
        for qid, q in req.questions.items()
    }

    start = time.perf_counter()
    result = _engine.decide(req.state, questions)
    latency_ms = round((time.perf_counter() - start) * 1000, 2)

    return DecideResponse(
        model=result.get("model") or _engine.model,
        answers=result.get("answers", {}),
        routing=result.get("routing"),
        usage=result.get("usage"),
        segments=result.get("segments"),
        latency_ms=latency_ms,
    )