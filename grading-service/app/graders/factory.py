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
    if p == "claude":
        from .claude_grader import ClaudeVisionGrader
        return ClaudeVisionGrader()
    if p == "gemini":
        from .gemini_grader import GeminiVisionGrader
        return GeminiVisionGrader()
    if p == "qwen":
        from .qwen_grader import QwenVisionGrader
        return QwenVisionGrader()
    if p == "gpt":
        from .gpt_grader import GptVisionGrader
        return GptVisionGrader()
    raise ValueError(f"Unknown MODEL_PROVIDER '{p}' — expected claude | gemini | qwen | gpt")