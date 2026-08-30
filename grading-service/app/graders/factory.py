"""Factory for instantiating the correct IVisionGrader based on config.

Nothing outside this module may import a concrete grader class directly —
the provider is always resolved through this factory.
"""
from __future__ import annotations

import logging

from .base import IVisionGrader
from ..config import settings

logger = logging.getLogger(__name__)


def get_grader(provider: str | None = None) -> IVisionGrader:
    """Return a fresh grader instance. Provider defaults to settings.MODEL_PROVIDER."""
    p = (provider or settings.MODEL_PROVIDER).lower()
    if p == "glm":
        from .glm_grader import GlmVisionGrader
        return GlmVisionGrader()
    if p == "gpt":
        from .gpt_grader import GptVisionGrader
        return GptVisionGrader()
    # Legacy alias: the GLM slot used to be called "qwen" (env QWEN_*) before
    # the slot was renamed. Keep old configs/DB rows working.
    if p == "qwen":
        logger.warning('{"event":"legacy_provider_alias","from":"qwen","to":"glm"}')
        from .glm_grader import GlmVisionGrader
        return GlmVisionGrader()
    raise ValueError(f"Unknown MODEL_PROVIDER '{p}' — expected glm | gpt")