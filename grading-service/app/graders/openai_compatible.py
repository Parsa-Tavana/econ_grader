"""OpenAI-compatible grading engine.

`OpenAICompatibleGrader` is the engine: any /chat/completions gateway with a
configurable auth scheme. Concrete slots (GLM, GPT) are thin subclasses in
their own modules injecting their own settings.
"""
from __future__ import annotations

import base64
import json
import logging
import time
from typing import Any

import httpx
from .base import (
    IVisionGrader, GradingResult, CriterionScore,
    ERROR_KIND_PARSE, ERROR_KIND_TIMEOUT, ModelOutputParseError,
)
from ..json_repair_util import parse_json_hardened
from ..prompts.loader import load_prompt
from ..config import settings

logger = logging.getLogger(__name__)

SYSTEM_PROMPT = (
    "You are a careful exam grader. Respond with JSON only. "
    "No markdown fences, no explanation outside JSON. "
    "Write ALL reasoning, criteria comments, and flagged_ambiguities in Persian (فارسی). "
    "The response JSON keys must stay exactly as specified (score, reasoning, criteria_scores, confidence, flagged_ambiguities)."
)


class OpenAICompatibleGrader(IVisionGrader):
    """Generic client for any OpenAI-compatible /chat/completions endpoint."""

    def __init__(
        self,
        *,
        provider_label: str,
        base_url: str,
        api_key: str,
        model: str,
        auth_scheme: str = "Bearer",
    ) -> None:
        self._provider_label = provider_label
        self._base_url = base_url.rstrip("/")
        self._api_key = api_key
        self._model = model
        self._auth_scheme = (auth_scheme or "Bearer").strip()

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
        document_only_rubric: bool = False,
    ) -> GradingResult:
        start_ms = time.time()
        model_name = self._model
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
        if document_only_rubric:
            # The structured JSON sees an empty "criteria" array when the rubric
            # ships ONLY as an uploaded document. Left alone, the "copy ids from
            # the JSON / never invent ids" instruction makes the model refuse or
            # award 0. Make the document authoritative instead.
            content.append({"type": "text", "text":
                "NOTE: The structured rubric JSON above is empty on purpose — this exam's "
                "rubric is defined ONLY in the 'Rubric document:' text above. Grade strictly "
                "against that document: use each criterion TITLE from the document as its "
                "criterion id and the document's own max scores. Do NOT refuse to grade and "
                "do NOT award zero merely because the structured criteria array is empty."
            })
        content.append({"type": "text", "text": prompt_text.format(
            question_text=question_text,
            rubric_json=json.dumps(rubric, indent=2),
            max_score=max_score,
        )})

        try:
            resp = httpx.post(
                f"{self._base_url}/chat/completions",
                headers={
                    "Authorization": f"{self._auth_scheme} {self._api_key}",
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
                timeout=140.0,
            )
            resp.raise_for_status()
            body = resp.json()
            # Some OpenAI-compatible gateways (e.g. api.cline.bot) wrap the
            # payload in an extra {"data": {...}} envelope — unwrap it so the
            # standard choices/usage access below keeps working everywhere.
            if isinstance(body, dict) and isinstance(body.get("data"), dict) and "choices" in body["data"]:
                body = body["data"]
            raw_text: str = body["choices"][0]["message"]["content"]
            latency_ms = int((time.time() - start_ms) * 1000)
            parsed = self._parse_response(raw_text)
            usage = body.get("usage", {})
            return GradingResult(
                provider=self._provider_label,
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
            logger.error('{"event":"parse_failure","provider":"%s","raw_preview":"%s"}',
                         self._provider_label, raw_text[:200].replace('"', "'"))
            return GradingResult(
                provider=self._provider_label, model_name=model_name, model_version=None,
                prompt_version=prompt_version, temperature=temperature, ai_score=0.0,
                reasoning="", raw_response=raw_text, latency_ms=int((time.time() - start_ms) * 1000),
                error=f"{self._provider_label} response parse failure: {exc}",
                error_kind=ERROR_KIND_PARSE,
            )
        except httpx.TimeoutException as exc:
            # Gateway latency is highly variable (same payload: 40s one call,
            # 5min the next) — tag as transient.
            logger.error('{"event":"upstream_timeout","provider":"%s"}', self._provider_label)
            return GradingResult(
                provider=self._provider_label, model_name=model_name, model_version=None,
                prompt_version=prompt_version, temperature=temperature, ai_score=0.0,
                reasoning="", raw_response="", latency_ms=int((time.time() - start_ms) * 1000),
                error=f"{self._provider_label} API failure: {exc}",
                error_kind=ERROR_KIND_TIMEOUT,
            )
        except Exception as exc:
            logger.exception("%s grading failed", self._provider_label)
            return GradingResult(
                provider=self._provider_label, model_name=model_name, model_version=None,
                prompt_version=prompt_version, temperature=temperature, ai_score=0.0,
                reasoning="", raw_response="", latency_ms=int((time.time() - start_ms) * 1000),
                error=f"{self._provider_label} API failure: {exc}",
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
