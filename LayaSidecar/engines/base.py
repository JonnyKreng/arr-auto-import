"""Decision engine abstraction and shared helpers for the /decide endpoint."""

from __future__ import annotations

import os
from abc import ABC, abstractmethod
from typing import Any, Dict, Mapping, Optional


def env_from(env: Optional[Mapping[str, str]]) -> Dict[str, str]:
    return dict(os.environ) if env is None else dict(env)


def normalize_answer(answer: Dict[str, Any]) -> Dict[str, Any]:
    """Normalize an engine answer into the plain JSON the worker expects.

    The worker gates the reject question on ``confidence``. As a safety net,
    synthesize it for noul answers with the same binary margin Von used
    (|2 * noul - 1|) when an engine forgot to include it.
    """
    data = dict(answer)
    if data.get("type") == "noul" and "confidence" not in data:
        noul = float(data.get("noul", 0.5))
        data["confidence"] = round(abs(2.0 * noul - 1.0), 4)
    return data


class DecisionEngine(ABC):
    """One decision backend behind the unchanged /decide contract.

    Startup is heavy (weights download + load), so :meth:`start` runs synchronously
    inside the FastAPI lifespan and flips :attr:`ready` only after the model is
    warmed up. :meth:`decide` may block; uvicorn dispatches the sync endpoint to a
    worker thread.
    """

    name: str = ""

    def __init__(self, env: Optional[Mapping[str, str]] = None):
        self._env = env_from(env)
        self._ready = False
        self._model_label = "unconfigured"
        self._backend_label = "unconfigured"

    @property
    def ready(self) -> bool:
        return self._ready

    @property
    def model(self) -> str:
        return self._model_label

    @property
    def backend(self) -> str:
        return self._backend_label

    async def start(self) -> None:
        raise NotImplementedError

    async def stop(self) -> None:
        self._ready = False

    @abstractmethod
    def decide(self, state: Any, questions: Dict[str, Any]) -> Dict[str, Any]:
        raise NotImplementedError