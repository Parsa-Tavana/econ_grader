"""IVisionGrader — the ONLY place a VLM provider SDK may be called.

Every concrete grader must implement `grade()` and return a GradingResult.
Nothing outside this interface (API layer, .NET app, UI) may call an
Anthropic/Google/OpenAI SDK directly.
"""
from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Any, Optional


@dataclass
class CriterionScore:
    criterion_id: str
    score: float
    max_score: float
    comment: Optional[str] = None


#: GradingResult.error_kind value for malformed model output (unparseable
#: JSON). Transient — callers may retry the run verbatim. Any other failure
#: (API outage, bad request) leaves error_kind unset.
ERROR_KIND_PARSE = "parse"


class ModelOutputParseError(ValueError):
    """Raised when a grader cannot extract usable JSON from model output."""


@dataclass
class GradingResult:
    """Normalized result returned by any provider."""
    provider: str
    model_name: str
    model_version: Optional[str]
    prompt_version: str
    temperature: float
    ai_score: float
    reasoning: str
    criteria_scores: list[CriterionScore] = field(default_factory=list)
    confidence: Optional[float] = None
    flagged_ambiguities: list[str] = field(default_factory=list)
    raw_response: str = ""
    input_tokens: int = 0
    output_tokens: int = 0
    latency_ms: int = 0
    error: Optional[str] = None
    error_kind: Optional[str] = None


class IVisionGrader(ABC):
    """Abstract base for all vision graders. Implementations MUST NOT leak
    teacher scores or rubric internals beyond what's in the prompt."""

    @abstractmethod
    def grade(
        self,
        *,
        question_text: str,
        question_images: list[bytes],
        rubric: dict[str, Any],
        answer_images: list[bytes],
        max_score: float,
        temperature: float,
        prompt_version: str,
    ) -> GradingResult:
        """Grade one student answer against one question + rubric.

        rubric: {"criteria": [{"id": "1a", "description": "...", "max_score": 2}, ...]}
        Returns GradingResult with ai_score in [0, max_score].
        """
        raise NotImplementedError