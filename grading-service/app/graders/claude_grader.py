"""ClaudeVisionGrader — official Anthropic SDK (anthropic>=0.42)."""
from __future__ import annotations

import base64
import json
import time
import logging
from typing import Any

from anthropic import Anthropic
from .base import IVisionGrader, GradingResult, CriterionScore
from ..prompts.loader import load_prompt
from ..config import settings

logger = logging.getLogger(__name__)


class ClaudeVisionGrader(IVisionGrader):
    def __init__(self) -> None:
        if not settings.ANTHROPIC_API_KEY:
            raise ValueError("ANTHROPIC_API_KEY is not set — cannot instantiate ClaudeVisionGrader")
        self._client = Anthropic(api_key=settings.ANTHROPIC_API_KEY)

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
        
        content: list[dict[str, Any]] = []
        for img in question_images:
            content.append({"type": "image", "source": {
                "type": "base64",
                "media_type": "image/png",
                "data": base64.b64encode(img).decode(),
            }})
        for img in answer_images:
            content.append({"type": "image", "source": {
                "type": "base64",
                "media_type": "image/png",
                "data": base64.b64encode(img).decode(),
            }})
        content.append({"type": "text", "text": prompt_text.format(
            question_text=question_text,
            rubric_json=json.dumps(rubric, indent=2),
            max_score=max_score,
        )})

        try:
            resp = self._client.messages.create(
                model=settings.MODEL_NAME,
                max_tokens=settings.DEFAULT_MAX_TOKENS,
                temperature=temperature,
                messages=[{"role": "user", "content": content}],
            )
            raw_text = "".join(b.text for b in resp.content if b.type == "text")
            latency_ms = int((time.time() - start_ms) * 1000)
            
            parsed = self._parse_response(raw_text)
            return GradingResult(
                provider="claude",
                model_name=settings.MODEL_NAME,
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
        except Exception as exc:
            logger.exception("Claude grading failed")
            return GradingResult(
                provider="claude", model_name=settings.MODEL_NAME, model_version=None,
                prompt_version=prompt_version, temperature=temperature, ai_score=0.0,
                reasoning="", raw_response="", latency_ms=int((time.time() - start_ms) * 1000),
                error=f"Claude API failure: {exc}",
            )

    @staticmethod
    def _parse_response(raw_text: str) -> dict[str, Any]:
        """Extract the JSON block from a model response."""
        # Strip markdown fences if present
        cleaned = raw_text.strip()
        if cleaned.startswith("```"):
            cleaned = "\n".join(cleaned.splitlines()[1:-1]).strip()
        try:
            data = json.loads(cleaned)
        except json.JSONDecodeError:
            # Fall back to first {...} block
            start = cleaned.find("{"); end = cleaned.rfind("}")
            if start >= 0 and end > start:
                data = json.loads(cleaned[start:end + 1])
            else:
                raise
        return data
