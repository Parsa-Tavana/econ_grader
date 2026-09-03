"""Langfuse tracing for AI calls (grading + rubric extraction).

Optional: with LANGFUSE_PUBLIC_KEY/SECRET_KEY unset in the environment,
every function here is a no-op and grading behaves exactly as before.
Traces are batched by the SDK; flush at request end so tail events are
not dropped when the container restarts.
"""
from __future__ import annotations

import logging
from typing import Any, Optional

from .config import settings

logger = logging.getLogger(__name__)

_client = None
_enabled = False


def _get_client():
    """Lazy-init the Langfuse client once; None when unconfigured."""
    global _client, _enabled
    if not settings.LANGFUSE_PUBLIC_KEY or not settings.LANGFUSE_SECRET_KEY:
        return None
    if _client is None:
        try:
            from langfuse import Langfuse
            _client = Langfuse(
                public_key=settings.LANGFUSE_PUBLIC_KEY,
                secret_key=settings.LANGFUSE_SECRET_KEY,
                host=settings.LANGFUSE_HOST,
            )
            _enabled = True
            logger.info('{"event":"langfuse_enabled","host":"%s"}' % settings.LANGFUSE_HOST)
        except Exception as exc:  # never let telemetry break grading
            logger.warning('{"event":"langfuse_init_failed","error":"%s"}' % exc)
    return _client


def trace_grade(
    *,
    req: dict[str, Any],
    result: Any,
    is_valid: bool,
    validation_errors: list[str],
    estimated_cost_usd: float,
    latency_ms: int,
) -> None:
    """One trace per grading call: inputs, output, tokens, cost, validity."""
    client = _get_client()
    if client is None:
        return
    try:
        score = float(result.ai_score) if result.ai_score is not None else None
        max_score = float(req.get("max_score") or 0) or None
        trace = client.trace(
            name="grade-answer",
            session_id=req.get("student_id"),
            user_id=req.get("student_id"),
            metadata={
                "question_id": req.get("question_id"),
                "run_id": result.run_id if hasattr(result, "run_id") else None,
                "max_score": req.get("max_score"),
                "prompt_version": req.get("prompt_version"),
                "is_valid": is_valid,
                "validation_errors": validation_errors[:5],
            },
            input={
                "question_text": (req.get("question_text") or "")[:4000],
                "answer_images": len(req.get("answer_image_paths") or []),
                "question_images": len(req.get("question_image_paths") or []),
                "rubric_criteria": len((req.get("rubric") or {}).get("criteria") or []),
            },
            output={
                "ai_score": score,
                "reasoning": (result.reasoning or "")[:4000],
                "criteria_scores": [
                    {"id": c.criterion_id, "score": c.score, "max_score": c.max_score}
                    for c in (result.criteria_scores or [])
                ],
                "flagged_ambiguities": (result.flagged_ambiguities or [])[:10],
            },
        )
        trace.generation(
            name=f"{result.provider}:{result.model_name}",
            model=result.model_name,
            input=(result.raw_response or "")[:2000],
            output=(result.raw_response or "")[:2000],
            usage={
                "input": result.input_tokens,
                "output": result.output_tokens,
                "total": (result.input_tokens or 0) + (result.output_tokens or 0),
                "unit": "TOKENS",
            },
            metadata={
                "latency_ms": latency_ms,
                "estimated_cost_usd": estimated_cost_usd,
                "temperature": result.temperature,
                "prompt_version": result.prompt_version,
                "error": result.error,
            },
        )
        if score is not None and max_score:
            trace.score(name="ai-score", value=round(score, 2))
        client.flush()
    except Exception as exc:  # telemetry must never fail a grade
        logger.warning('{"event":"langfuse_trace_failed","error":"%s"}' % exc)


def trace_extract(
    *,
    document_name: str,
    result: Any,
    is_valid: bool,
    warnings: list[str],
    latency_ms: int,
) -> None:
    """One trace per rubric-extraction call."""
    client = _get_client()
    if client is None:
        return
    try:
        trace = client.trace(
            name="extract-rubric",
            metadata={
                "document_name": document_name,
                "is_valid": is_valid,
                "warnings": warnings[:5],
            },
            input={
                "document_text_chars": len(getattr(result, "raw_response", "") or ""),
                "prompt_version": result.prompt_version,
            },
            output={
                "questions_found": len((result.parsed or {}).get("questions", []))
                if isinstance(result.parsed, dict) else None,
            },
        )
        trace.generation(
            name=f"{result.provider}:{result.model_name}",
            model=result.model_name,
            usage={
                "input": result.input_tokens,
                "output": result.output_tokens,
                "total": (result.input_tokens or 0) + (result.output_tokens or 0),
                "unit": "TOKENS",
            },
            metadata={"latency_ms": latency_ms, "error": result.error},
        )
        client.flush()
    except Exception as exc:
        logger.warning('{"event":"langfuse_trace_failed","error":"%s"}' % exc)
