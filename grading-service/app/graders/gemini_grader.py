"""GeminiVisionGrader — official Google GenAI SDK (google-genai>=0.6)."""
from __future__ import annotations

import json
import time
import logging
from typing import Any

from google import genai
from google.genai import types
from .base import (
    IVisionGrader, GradingResult, CriterionScore,
    ERROR_KIND_PARSE, ModelOutputParseError,
)
from ..json_repair_util import parse_json_hardened
from ..prompts.loader import load_prompt
from ..config import settings

logger = logging.getLogger(__name__)


class GeminiVisionGrader(IVisionGrader):
    def __init__(self) -> None:
        if not settings.GOOGLE_API_KEY:
            raise ValueError("GOOGLE_API_KEY is not set — cannot instantiate GeminiVisionGrader")
        self._client = genai.Client(api_key=settings.GOOGLE_API_KEY)

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
        start_ms = time.time()
        prompt_text = load_prompt(prompt_version)
        
        parts: list[Any] = []
        for img in question_images:
            parts.append(types.Part.from_bytes(data=img, mime_type="image/png"))
        for img in answer_images:
            parts.append(types.Part.from_bytes(data=img, mime_type="image/png"))
        parts.append(types.Part.from_text(text=prompt_text.format(
            question_text=question_text,
            rubric_json=json.dumps(rubric, indent=2),
            max_score=max_score,
        )))

        try:
            resp = self._client.models.generate_content(
                model=settings.MODEL_NAME,
                contents=parts,
                config=types.GenerateContentConfig(
                    temperature=temperature,
                    max_output_tokens=settings.DEFAULT_MAX_TOKENS,
                ),
            )
            raw_text = resp.text or ""
            latency_ms = int((time.time() - start_ms) * 1000)
            
            parsed = self._parse_response(raw_text)
            return GradingResult(
                provider="gemini",
                model_name=settings.MODEL_NAME,
                model_version=resp.model_version,
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
                input_tokens=resp.usage_metadata.prompt_token_count,
                output_tokens=resp.usage_metadata.candidates_token_count,
                latency_ms=latency_ms,
            )
        except ModelOutputParseError as exc:
            logger.error('{"event":"parse_failure","provider":"gemini","raw_preview":"%s"}',
                         raw_text[:200].replace('"', "'"))
            return GradingResult(
                provider="gemini", model_name=settings.MODEL_NAME, model_version=None,
                prompt_version=prompt_version, temperature=temperature, ai_score=0.0,
                reasoning="", raw_response=raw_text, latency_ms=int((time.time() - start_ms) * 1000),
                error=f"Gemini response parse failure: {exc}",
                error_kind=ERROR_KIND_PARSE,
            )
        except Exception as exc:
            logger.exception("Gemini grading failed")
            return GradingResult(
                provider="gemini", model_name=settings.MODEL_NAME, model_version=None,
                prompt_version=prompt_version, temperature=temperature, ai_score=0.0,
                reasoning="", raw_response="", latency_ms=int((time.time() - start_ms) * 1000),
                error=f"Gemini API failure: {exc}",
            )

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