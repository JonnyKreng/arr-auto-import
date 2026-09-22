"""Engine registry. DECISION_ENGINE selects which backend serves /decide."""

from __future__ import annotations

import os
from typing import Dict, Mapping, Optional

from .base import DecisionEngine
from .laya_engine import LayaEngine
from .llm_engine import LlmEngine

ENGINES: Dict[str, type[DecisionEngine]] = {
    "laya": LayaEngine,
    "llm": LlmEngine,
}


def engine_from_env(env: Optional[Mapping[str, str]] = None) -> DecisionEngine:
    env = dict(os.environ) if env is None else dict(env)
    name = env.get("DECISION_ENGINE", "laya").strip().lower()
    if name not in ENGINES:
        raise ValueError(f"Unknown DECISION_ENGINE {name!r}; choose from {sorted(ENGINES)}")
    return ENGINES[name](env)


__all__ = ["DecisionEngine", "ENGINES", "engine_from_env", "LayaEngine", "LlmEngine"]