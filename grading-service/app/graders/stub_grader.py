"""Stub grader for QA / integration testing.

Returns deterministic valid JSON with realistic latency — no network calls.
Control via env vars:
  STUB_DELAY_SEC      – simulated latency (default 1.5)
  STUB_FAILURE_MODE   – "" (none) | "timeout" | "bad_json" | "error"
"""
from __future__ import annotations

import json
import time
import uuid
from typing import Any

from ..config import settings
from .base import IVisionGrader, GradingResult, CriterionScore, ERROR_KIND_PARSE

FAILURE_MODES = {"", "timeout", "bad_json", "error"}


class StubVisionGrader(IVisionGrader):
    """Deterministic grader for testing the pipeline end-to-end without LLM calls."""

    def __init__(self):
        self._delay = float(getattr(settings, "STUB_DELAY_SEC", 1.5))
        self._failure = getattr(settings, "STUB_FAILURE_MODE", "")

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
        t0 = time.time()

        # --- simulate latency (cap at 120 s for the "timeout" test) ---
        actual_delay = 120.0 if self._failure == "timeout" else self._delay
        time.sleep(min(actual_delay, 120.0))

        latency_ms = int((time.time() - t0) * 1000)

        # --- error mode ---
        if self._failure == "error":
            return GradingResult(
                provider="stub",
                model_name="stub-v1",
                model_version=None,
                prompt_version=prompt_version,
                temperature=temperature,
                ai_score=0,
                reasoning="",
                criteria_scores=[],
                raw_response="",
                latency_ms=latency_ms,
                error="Simulated provider error (STUB_FAILURE_MODE=error)",
            )

        # --- bad_json mode ---
        if self._failure == "bad_json":
            return GradingResult(
                provider="stub",
                model_name="stub-v1",
                model_version=None,
                prompt_version=prompt_version,
                temperature=temperature,
                ai_score=0,
                reasoning="",
                criteria_scores=[],
                raw_response="THIS IS NOT VALID JSON {{{{",
                latency_ms=latency_ms,
                error_kind=ERROR_KIND_PARSE,
            )

        # --- happy path: deterministic score split across criteria ---
        criteria_req = rubric.get("criteria", [])
        scores: list[CriterionScore] = []
        # Build a lookup for comment to avoid duplicating description text
        comment_map = {c["id"]: c.get("description", "")[:40] for c in criteria_req}
        total = 0.0
        for c in criteria_req:
            ms = float(c.get("max_score", 0))
            # deterministic: give 70% of each criterion's max (capped to 2 decimals)
            s = round(ms * 0.7, 2)
            scores.append(CriterionScore(
                criterion_id=c["id"],
                score=s,
                max_score=ms,
                comment=f"stub: {comment_map.get(c['id'], '')}",
            ))
            total += s

        # raw_response must contain ALL criteria_scores entries so validation passes.
        # Python validation.py line 65 expects 'id' key (not criterion_id) in each entry.
        raw_criterions = [
            {
                "id": s.criterion_id,
                "score": s.score,
                "max_score": s.max_score,
                "comment": s.comment,
            }
            for s in scores
        ]
        raw = {
            "score": total,
            "reasoning": "Stub grading — deterministic 70%% per criterion",
            "criteria_scores": raw_criterions,
            "confidence": 1.0,
            "flagged_ambiguities": [],
        }

        return GradingResult(
            provider="stub",
            model_name="stub-v1",
            model_version=None,
            prompt_version=prompt_version,
            temperature=temperature,
            ai_score=total,
            reasoning=raw["reasoning"],
            criteria_scores=scores,
            confidence=1.0,
            flagged_ambiguities=[],
            raw_response=json.dumps(raw),
            input_tokens=1200,
            output_tokens=800,
            latency_ms=latency_ms,
        )
