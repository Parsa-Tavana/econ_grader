"""QwenVisionGrader — self-hosted Qwen3-VL via OpenAI-compatible vLLM endpoint."""
from __future__ import annotations

import base64
import json
import logging
import time
from typing import Any

import httpx
from .base import (
    IVisionGrader, GradingResult, CriterionScore,
    ERROR_KIND_PARSE, ModelOutputParseError,
)
from ..json_repair_util import parse_json_hardened
from ..prompts.loader import load_prompt
from ..config import settings

logger = logging.getLogger(__name__)

SYSTEM_PROMPT = (
    "You are a careful exam grader. Respond with JSON only. "
    "No markdown fences, no explanation outside JSON."
)


class QwenVisionGrader(IVisionGrader):
    """Calls a self-hosted vLLM/OpenAI-compatible endpoint at QWEN_BASE_URL."""

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
        model_name = settings.QWEN_MODEL or settings.MODEL_NAME
        prompt_text = load_prompt(prompt_version)
        rubric_images = rubric_images or []

        content: list[dict[str, Any]] = []
        # Order matters: question paper → student answer → rubric document → prompt.
        for data, media_type in [*question_images, *answer_images, *rubric_images]:
            content.append({"type": "image_url", "image_url": {
                "url": f"data:{media_type};base64,{base64.b64encode(data).decode()}"
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
            resp = httpx.post(
                f"{settings.QWEN_BASE_URL.rstrip('/')}/chat/completions",
                headers={
                    "Authorization": f"Bearer {settings.QWEN_API_KEY}",
                    "Content-Type": "application/json",
                },
                json={
                    "model": model_name,
                    "messages": [
                        {"role": "system", "content": SYSTEM_PROMPT},
                        {"role": "user", "content": content},
                    ],
                    "temperature": temperature,
                    "max_tokens": settings.DEFAULT_MAX_TOKENS,
                },
                timeout=120.0,
            )
            resp.raise_for_status()
            body = resp.json()
            raw_text: str = body["choices"][0]["message"]["content"]
            latency_ms = int((time.time() - start_ms) * 1000)
            parsed = self._parse_response(raw_text)
            usage = body.get("usage", {})
            return GradingResult(
                provider="qwen",
                model_name=model_name,
                model_version=None,
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
                input_tokens=usage.get("prompt_tokens", 0),
                output_tokens=usage.get("completion_tokens", 0),
                latency_ms=latency_ms,
            )
        except ModelOutputParseError as exc:
            logger.error('{"event":"parse_failure","provider":"qwen","raw_preview":"%s"}',
                         raw_text[:200].replace('"', "'"))
            return GradingResult(
                provider="qwen", model_name=settings.MODEL_NAME, model_version=None,
                prompt_version=prompt_version, temperature=temperature, ai_score=0.0,
                reasoning="", raw_response=raw_text, latency_ms=int((time.time() - start_ms) * 1000),
                error=f"Qwen response parse failure: {exc}",
                error_kind=ERROR_KIND_PARSE,
            )
        except Exception as exc:
            logger.exception("Qwen grading failed")
            return GradingResult(
                provider="qwen", model_name=model_name, model_version=None,
                prompt_version=prompt_version, temperature=temperature, ai_score=0.0,
                reasoning="", raw_response="", latency_ms=int((time.time() - start_ms) * 1000),
                error=f"Qwen API failure: {exc}",
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