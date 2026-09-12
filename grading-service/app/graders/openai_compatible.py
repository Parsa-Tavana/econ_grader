"""OpenAI-compatible grading engine.

`OpenAICompatibleGrader` is the engine: any /chat/completions gateway with a
configurable auth scheme. Concrete slots (GLM, GPT) are thin subclasses in
their own modules injecting their own settings.

Requests use SSE streaming by default. Reason: fronting gateways (e.g.
ArvanCloud) cut connections that go silent for ~60s with a 504 or a truncated
chunked body, while vision grading legitimately takes minutes before the
FIRST token. Streaming keeps bytes flowing and defeats the idle timeout.
Set the env var GLM_STREAMING=0 (or GPT_STREAMING=0) to fall back to
non-streaming for a provider that mishandles SSE.
"""
from __future__ import annotations

import base64
import json
import logging
import time
from typing import Any

import httpx
from .base import (
    IVisionGrader, GradingResult, ExtractionResult, CriterionScore,
    ERROR_KIND_PARSE, ERROR_KIND_TIMEOUT, ModelOutputParseError,
)
from ..json_repair_util import parse_json_hardened
from ..prompts.loader import load_prompt
from ..config import settings

logger = logging.getLogger(__name__)


def _parse_response_body(resp: httpx.Response) -> dict[str, Any]:
    """Decode a /chat/completions JSON body defensively.

    Some gateways append SSE framing to non-streaming replies — e.g. 9router
    ends the body with a stray "data: [DONE]\\n\\n" line after the JSON object —
    which makes httpx's strict resp.json() raise JSONDecodeError("Extra data").
    Decode with raw_decode (first JSON value wins) and unwrap the {"data":...}
    envelope some gateways (e.g. api.cline.bot) add.
    """
    raw = resp.text
    try:
        body, _ = json.JSONDecoder().raw_decode(raw.lstrip())
    except json.JSONDecodeError:
        body = resp.json()  # surfaces the original error message
    if isinstance(body, dict) and isinstance(body.get("data"), dict) and "choices" in body["data"]:
        body = body["data"]
    return body


def _parse_stream_body(resp: httpx.Response) -> dict[str, Any]:
    """Consume an SSE /chat/completions stream and reconstruct a single
    response-shaped dict (same keys the non-streaming path returns).

    - accumulates choices[*].delta.content into message.content
    - ignores delta.reasoning_content (thinking models) — it must not leak
      into the graded JSON
    - picks up usage from the final chunk when the gateway sends it
      (OpenAI-compatible gateways honour stream_options.include_usage)
    - raises httpx.HTTPStatusError-shaped semantics: a non-200 start or an
      in-band {"error": ...} chunk is surfaced as an exception so callers'
      existing error/retry handling keeps working
    """
    if resp.status_code != 200:
        resp.raise_for_status()

    content_parts: list[str] = []
    usage: dict[str, Any] = {}
    finish_reason: str | None = None
    model_name: str | None = None

    for line in resp.iter_lines():
        if not line or not line.startswith("data:"):
            continue
        payload = line[len("data:"):].strip()
        if payload == "[DONE]":
            break
        try:
            chunk = json.loads(payload)
        except json.JSONDecodeError:
            continue  # tolerate keep-alives / partial frames
        if not isinstance(chunk, dict):
            continue
        if isinstance(chunk.get("error"), (dict, str)):
            err = chunk["error"]
            detail = err.get("message", str(err)) if isinstance(err, dict) else str(err)
            raise RuntimeError(f"gateway stream error: {detail}")
        model_name = model_name or chunk.get("model")
        if isinstance(chunk.get("usage"), dict) and chunk["usage"]:
            usage = chunk["usage"]
        for choice in chunk.get("choices") or []:
            delta = choice.get("delta") or {}
            piece = delta.get("content")
            if piece:
                content_parts.append(piece)
            fr = choice.get("finish_reason")
            if fr:
                finish_reason = fr

    raw_text = "".join(content_parts)
    if not raw_text and not usage:
        # Stream ended with nothing at all — treat like an empty non-stream body.
        raise RuntimeError("gateway stream produced no content")

    return {
        "model": model_name,
        "choices": [{
            "message": {"content": raw_text},
            "finish_reason": finish_reason,
        }],
        "usage": usage,
    }

SYSTEM_PROMPT = (
    "You are a careful exam grader. Respond with JSON only. "
    "No markdown fences, no explanation outside JSON. "
    "Write ALL reasoning, criteria comments, and flagged_ambiguities in Persian (فارسی). "
    "The response JSON keys must stay exactly as specified (score, reasoning, criteria_scores, confidence, flagged_ambiguities)."
)

EXTRACTION_SYSTEM_PROMPT = (
    "You extract exam questions and grading rubrics from grading-key documents. "
    "Respond with JSON only. No markdown fences, no explanation outside JSON. "
    "Copy all question and criterion text VERBATIM in the document's original "
    "language (Persian stays Persian — never translate). "
    "The response JSON keys must stay exactly as specified (questions, number, text, max_score, criteria, id, description)."
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
        streaming: bool = True,
    ) -> None:
        self.provider_label = provider_label
        self.model_name = model
        self._provider_label = provider_label
        self._base_url = base_url.rstrip("/")
        self._api_key = api_key
        self._model = model
        self._auth_scheme = (auth_scheme or "Bearer").strip()
        self._streaming = streaming

    def _chat(
        self,
        *,
        system_prompt: str,
        content: list[dict[str, Any]],
        temperature: float,
        max_tokens: int,
        non_stream_timeout: float,
    ) -> dict[str, Any]:
        """POST /chat/completions and return a response-shaped dict
        ({choices:[{message:{content}}], usage:{...}}) regardless of mode.

        Streaming is the default (see module docstring): bytes flow while the
        model thinks, so an idle-timeout gateway never kills the request. The
        read timeout still applies per-chunk — a gateway that goes silent
        mid-stream fails fast and the caller's transient-retry handles it.
        """
        payload: dict[str, Any] = {
            "model": self._model,
            "messages": [
                {"role": "system", "content": system_prompt},
                {"role": "user", "content": content},
            ],
            "temperature": temperature,
            "max_tokens": max_tokens,
        }
        headers = {
            "Authorization": f"{self._auth_scheme} {self._api_key}",
            "Content-Type": "application/json",
        }
        if self._streaming:
            payload["stream"] = True
            # Gateways that honour it put usage in the final chunk; those that
            # don't simply return no usage and token counts stay zero.
            payload["stream_options"] = {"include_usage": True}
            # No overall cap: .NET's 315s attempt timeout is the budget.
            # read=75 → gap between chunks; write=60 → uploading base64 pages.
            timeout = httpx.Timeout(connect=15.0, read=75.0, write=60.0, pool=15.0)
            with httpx.stream(
                "POST", f"{self._base_url}/chat/completions",
                headers=headers, json=payload, timeout=timeout,
            ) as resp:
                if resp.status_code != 200:
                    resp.read()  # pull the error body for the log
                body = _parse_stream_body(resp)
        else:
            resp = httpx.post(
                f"{self._base_url}/chat/completions",
                headers=headers, json=payload, timeout=non_stream_timeout,
            )
            resp.raise_for_status()
            body = _parse_response_body(resp)
        return body

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
    ) -> GradingResult:
        start_ms = time.time()
        model_name = self._model
        prompt_text = load_prompt(prompt_version)

        content: list[dict[str, Any]] = []
        # Order matters: question paper → student answer → prompt.
        # Each image group gets an explicit TEXT label. Without one the model
        # receives an anonymous stack of pages and can attribute printed
        # question-paper figures (graphs, tables) to the student's own work
        # (observed live: a model "saw a diagram in the student answer" that
        # was actually printed on the question paper).
        def _img(data: bytes, media_type: str) -> dict[str, Any]:
            return {"type": "image_url", "image_url": {
                "url": f"data:{media_type};base64,{base64.b64encode(data).decode()}"
            }}

        def _txt(value: str) -> dict[str, Any]:
            return {"type": "text", "text": value}

        if question_images:
            content.append(_txt(
                "IMAGES OF THE QUESTION PAPER (the printed exam sheet — NOT the student's work):"
            ))
            content.extend(_img(d, mt) for d, mt in question_images)
        if answer_images:
            content.append(_txt(
                "IMAGES OF THE STUDENT'S HANDWRITTEN ANSWER — grade ONLY what the student "
                "actually wrote on these pages; figures/diagrams printed on the question "
                "paper above are NOT the student's work:"
            ))
            total_answer_pages = len(answer_images)
            for i, (d, mt) in enumerate(answer_images, start=1):
                content.append(_txt(f"[Student answer — page {i} of {total_answer_pages}]"))
                content.append(_img(d, mt))
        if extra_text.strip():
            # Extracted text from the student's typed answer documents
            content.append(_txt(f"Student typed answer documents:\n{extra_text.strip()}"))
        content.append({"type": "text", "text": prompt_text.format(
            question_text=question_text,
            rubric_json=json.dumps(rubric, indent=2),
            max_score=max_score,
        )})

        try:
            body = self._chat(
                system_prompt=SYSTEM_PROMPT,
                content=content,
                temperature=temperature,
                max_tokens=settings.DEFAULT_MAX_TOKENS,
                non_stream_timeout=140.0,
            )
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

    def extract(
        self,
        *,
        document_text: str,
        document_images: list[tuple[bytes, str]],
        document_name: str,
        temperature: float,
        prompt_version: str,
    ) -> ExtractionResult:
        """Extract all questions + rubric criteria from one exam-wide rubric
        document. Same request shape as grade(); bigger token budget and a
        longer timeout because grading-key PDFs produce large outputs and the
        page images are heavy."""
        start_ms = time.time()
        model_name = self._model
        prompt_text = load_prompt(prompt_version)

        content: list[dict[str, Any]] = []
        total_pages = len(document_images)
        for i, (d, mt) in enumerate(document_images, start=1):
            content.append({"type": "text", "text": f"[Rubric key — page {i} of {total_pages}]"})
            content.append({"type": "image_url", "image_url": {
                "url": f"data:{mt};base64,{base64.b64encode(d).decode()}"
            }})
        if document_text.strip():
            content.append({"type": "text", "text":
                f"Extracted text of the rubric document:\n{document_text.strip()}"
            })
        content.append({"type": "text", "text": prompt_text.format(
            document_name=document_name or "rubric document",
            document_pages=total_pages,
            document_text=document_text.strip(),
        )})

        try:
            body = self._chat(
                system_prompt=EXTRACTION_SYSTEM_PROMPT,
                content=content,
                temperature=temperature,
                max_tokens=settings.EXTRACTION_MAX_TOKENS,
                non_stream_timeout=settings.EXTRACTION_TIMEOUT_SECONDS,
            )
            raw_text: str = body["choices"][0]["message"]["content"]
            latency_ms = int((time.time() - start_ms) * 1000)
            parsed = self._parse_response(raw_text)
            usage = body.get("usage", {})
            return ExtractionResult(
                provider=self._provider_label,
                model_name=model_name,
                model_version=None,
                prompt_version=prompt_version,
                raw_response=raw_text,
                parsed=parsed,
                input_tokens=usage.get("prompt_tokens", 0),
                output_tokens=usage.get("completion_tokens", 0),
                latency_ms=latency_ms,
            )
        except ModelOutputParseError as exc:
            logger.error('{"event":"extraction_parse_failure","provider":"%s","raw_preview":"%s"}',
                         self._provider_label, raw_text[:200].replace('"', "'"))
            return ExtractionResult(
                provider=self._provider_label, model_name=model_name, model_version=None,
                prompt_version=prompt_version,
                raw_response=raw_text,
                latency_ms=int((time.time() - start_ms) * 1000),
                error=f"{self._provider_label} extraction response parse failure: {exc}",
                error_kind=ERROR_KIND_PARSE,
            )
        except httpx.TimeoutException as exc:
            logger.error('{"event":"extraction_upstream_timeout","provider":"%s"}', self._provider_label)
            return ExtractionResult(
                provider=self._provider_label, model_name=model_name, model_version=None,
                prompt_version=prompt_version,
                latency_ms=int((time.time() - start_ms) * 1000),
                error=f"{self._provider_label} API failure: {exc}",
                error_kind=ERROR_KIND_TIMEOUT,
            )
        except Exception as exc:
            logger.exception("%s extraction failed", self._provider_label)
            return ExtractionResult(
                provider=self._provider_label, model_name=model_name, model_version=None,
                prompt_version=prompt_version,
                latency_ms=int((time.time() - start_ms) * 1000),
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
