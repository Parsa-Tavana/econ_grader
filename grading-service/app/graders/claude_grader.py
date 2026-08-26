"""ClaudeVisionGrader — official Anthropic SDK (anthropic>=0.42)."""
from __future__ import annotations

import base64
import json
import time
import logging
from typing import Any

from anthropic import Anthropic
from .base import (
    IVisionGrader, GradingResult, CriterionScore,
    ERROR_KIND_PARSE, ModelOutputParseError,
)
from ..json_repair_util import parse_json_hardened
from ..prompts.loader import load_prompt
from ..config import settings

logger = logging.getLogger(__name__)


def _langfuse_client():
    """Shared Langfuse SDK client, or None when tracing is not configured.

    Opt-in via LANGFUSE_PUBLIC_KEY/SECRET_KEY; every failure to initialize or
    ingest is swallowed — observability must never break grading.
    """
    if not (settings.LANGFUSE_PUBLIC_KEY and settings.LANGFUSE_SECRET_KEY):
        return None
    try:
        from langfuse import Langfuse
        return Langfuse(
            public_key=settings.LANGFUSE_PUBLIC_KEY,
            secret_key=settings.LANGFUSE_SECRET_KEY,
            host=settings.LANGFUSE_HOST,
        )
    except Exception:
        logger.exception("Langfuse init failed — continuing without LLM tracing")
        return None


class ClaudeVisionGrader(IVisionGrader):
    # One client per process; the SDK batches and flushes on its own.
    _lf = _langfuse_client()

    def __init__(self) -> None:
        if not settings.ANTHROPIC_API_KEY:
            raise ValueError("ANTHROPIC_API_KEY is not set — cannot instantiate ClaudeVisionGrader")
        self._client = Anthropic(api_key=settings.ANTHROPIC_API_KEY)

    def grade(
        self,
        *,
        question_text: str,
        question_images: list[tuple[bytes, str]],
        rubric: dict[str, Any],
        answer_images: list[tuple[bytes, str]],
        max_score: float,
        temperature: float,
        prompt_version: str,
        extra_text: str = "",
        rubric_text: str = "",
        rubric_images: list[tuple[bytes, str]] | None = None,
    ) -> GradingResult:
        start_ms = time.time()
        model_name = settings.CLAUDE_MODEL or settings.MODEL_NAME
        prompt_text = load_prompt(prompt_version)
        rubric_images = rubric_images or []

        content: list[dict[str, Any]] = []
        # Order matters: question paper → student answer → rubric document → prompt.
        for data, media_type in [*question_images, *answer_images, *rubric_images]:
            content.append({"type": "image", "source": {
                "type": "base64",
                "media_type": media_type,
                "data": base64.b64encode(data).decode(),
            }})
        if extra_text.strip():
            # Extracted text from the student's typed answer documents
            content.append({"type": "text", "text": f"Student typed answer documents:\n{extra_text.strip()}"})
        if rubric_text.strip():
            # Extracted text from the uploaded rubric document (DOCX/XLSX)
            content.append({"type": "text", "text": f"Rubric document:\n{rubric_text.strip()}"})
        content.append({"type": "text", "text": prompt_text.format(
            question_text=question_text,
            rubric_json=json.dumps(rubric, indent=2),
            max_score=max_score,
        )})

        try:
            resp = self._client.messages.create(
                model=model_name,
                max_tokens=settings.DEFAULT_MAX_TOKENS,
                temperature=temperature,
                messages=[{"role": "user", "content": content}],
            )
            raw_text = "".join(b.text for b in resp.content if b.type == "text")
            if not raw_text.strip() and getattr(resp, "stop_reason", None) == "max_tokens":
                # All tokens were consumed by thinking (or other non-text blocks)
                # before any JSON was emitted — a clearer error than a bare
                # JSONDecodeError, and actionable: raise DEFAULT_MAX_TOKENS.
                raise RuntimeError(
                    "Model produced no text (stop_reason=max_tokens) — "
                    "output budget exhausted by reasoning; increase DEFAULT_MAX_TOKENS"
                )
            latency_ms = int((time.time() - start_ms) * 1000)

            parsed = self._parse_response(raw_text)
            self._trace_generation(
                model_name=model_name, prompt_version=prompt_version,
                question_text=question_text, prompt_text=prompt_text,
                raw_text=raw_text, parsed=parsed,
                input_tokens=resp.usage.input_tokens,
                output_tokens=resp.usage.output_tokens,
                latency_ms=latency_ms,
            )
            return GradingResult(
                provider="claude",
                model_name=model_name,
                model_version=getattr(resp, "model", None),
                prompt_version=prompt_version,
                temperature=temperature,
                ai_score=parsed.get("score", 0.0),
                reasoning=parsed.get("reasoning", ""),
                criteria_scores=[
                    CriterionScore(
                        criterion_id=c["id"],
                        score=float(c.get("score", 0)),
                        max_score=float(c.get("max_score", 0)),
                        comment=c.get("comment"),
                    )
                    for c in parsed.get("criteria_scores", [])
                    if isinstance(c, dict)
                ],
                confidence=parsed.get("confidence"),
                flagged_ambiguities=parsed.get("flagged_ambiguities", []),
                raw_response=raw_text,
                input_tokens=resp.usage.input_tokens,
                output_tokens=resp.usage.output_tokens,
                latency_ms=latency_ms,
            )
        except ModelOutputParseError as exc:
            logger.error('{"event":"parse_failure","provider":"claude","raw_preview":"%s"}',
                         raw_text[:200].replace('"', "'"))
            return GradingResult(
                provider="claude", model_name=settings.MODEL_NAME, model_version=None,
                prompt_version=prompt_version, temperature=temperature, ai_score=0.0,
                reasoning="", raw_response=raw_text, latency_ms=int((time.time() - start_ms) * 1000),
                error=f"Claude response parse failure: {exc}",
                error_kind=ERROR_KIND_PARSE,
            )
        except Exception as exc:
            logger.exception("Claude grading failed")
            return GradingResult(
                provider="claude", model_name=model_name, model_version=None,
                prompt_version=prompt_version, temperature=temperature, ai_score=0.0,
                reasoning="", raw_response="", latency_ms=int((time.time() - start_ms) * 1000),
                error=f"Claude API failure: {exc}",
            )

    def _trace_generation(
        self, *, model_name: str, prompt_version: str,
        question_text: str, prompt_text: str, raw_text: str,
        parsed: dict[str, Any], input_tokens: int, output_tokens: int,
        latency_ms: int,
    ) -> None:
        """Best-effort Langfuse trace of one successful grading call."""
        if self._lf is None:
            return
        try:
            trace = self._lf.trace(
                name="grade-answer",
                metadata={"prompt_version": prompt_version},
            )
            trace.generation(
                name=f"claude/{model_name}",
                model=model_name,
                input=[
                    {"role": "user",
                     "content": question_text or "(image-only answer)"},
                ],
                output=raw_text,
                usage={"input": input_tokens, "output": output_tokens},
                # Cost is derived from the model price table in the UI.
                metadata={
                    "latency_ms": latency_ms,
                    "ai_score": parsed.get("score"),
                    "criteria_count": len(parsed.get("criteria_scores", [])),
                    "prompt_preview": prompt_text[:500],
                },
            )
        except Exception:
            logger.exception("Langfuse trace failed — grading unaffected")

    @staticmethod
    def _parse_response(raw_text: str) -> dict[str, Any]:
        """Parse the model response, applying staged JSON repairs.

        Raises ModelOutputParseError on unparseable output so grade() can tag
        the run as a transient parse failure (retryable), not an API failure.
        """
        parsed, err = parse_json_hardened(raw_text)
        if parsed is None:
            raise ModelOutputParseError(err or "no content")
        return parsed
