"""Grading Service — FastAPI application.

Internal REST API called by the .NET app (never exposed to the browser).
Logging: structured JSON logs with queryable fields.
"""
from __future__ import annotations

import json
import logging
import sys
import time
import uuid
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any

from fastapi import Depends, FastAPI, HTTPException, Request
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import JSONResponse

from .config import settings
from .graders.factory import get_grader
from .graders.base import ERROR_KIND_PARSE, ERROR_KIND_TIMEOUT
from .schemas import (
    GradeRequest, GradeResponse, CriterionOut, HealthResponse, EvaluationRequest,
    ExtractionRequest, ExtractionResponse, ExtractedQuestionOut, ExtractedCriterionOut,
)
from .validation import validate_grading_response, parse_json_safe
from .extraction_validation import validate_extraction
from .attachments import prepare_attachments
from .cost import estimate_cost
from .prompts.loader import list_prompt_versions, load_prompt
from .evaluation import compute_metrics, aggregate_by_provider
from .internal_auth import require_internal_key

logging.basicConfig(
    level=getattr(logging, settings.LOG_LEVEL.upper(), logging.INFO),
    format='{"ts":"%(asctime)s","level":"%(levelname)s","logger":"%(name)s","msg": %(message)s}',
    handlers=[logging.StreamHandler(sys.stdout)],
)
logger = logging.getLogger("grading-service")


@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info('{"event":"startup","provider":"%s","model":"%s"}' % (settings.MODEL_PROVIDER, settings.MODEL_NAME))
    yield
    logger.info('{"event":"shutdown"}')


app = FastAPI(
    title="EconGrader — Grading Service",
    description="Internal grading microservice. Do NOT expose to the public internet.",
    version="1.0.0",
    lifespan=lifespan,
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_methods=["*"],
    allow_headers=["*"],
)


def _log_request_response(req: dict, resp: GradeResponse | None, raw_text: str | None, latency_ms: int):
    record = {
        "event": "grading",
        "provider": resp.provider if resp else "unknown",
        "model": resp.model_name if resp else settings.MODEL_NAME,
        "prompt_version": req.get("prompt_version"),
        "student_id": req.get("student_id"),
        "question_id": req.get("question_id"),
        "latency_ms": latency_ms,
        "is_valid": resp.is_valid if resp else False,
        "error": resp.error if resp else None,
        "validation_errors": resp.validation_errors if resp else [],
        "input_tokens": resp.input_tokens if resp else 0,
        "output_tokens": resp.output_tokens if resp else 0,
        "cost_usd": resp.estimated_cost_usd if resp else 0,
        "raw_len": len(raw_text or ""),
    }
    logger.info(json.dumps(record))


@app.get("/health", response_model=HealthResponse, tags=["ops"])
def health():
    # Report the ACTIVE slot's identity (resolved through the factory) rather
    # than the legacy MODEL_NAME fallback — with MODEL_PROVIDER=gpt the model
    # is GPT_MODEL, not MODEL_NAME.
    try:
        grader = get_grader()
        provider, model_name = grader.provider_label, grader.model_name
    except Exception as exc:  # misconfigured provider must not fail the probe
        logger.error('{"event":"health_grader_error","error":"%s"}' % exc)
        provider, model_name = settings.MODEL_PROVIDER, settings.MODEL_NAME
    return HealthResponse(
        status="ok",
        provider=provider,
        model_name=model_name,
        prompts_available=list_prompt_versions(),
    )


@app.get("/prompts", tags=["prompts"], dependencies=[Depends(require_internal_key)])
def list_prompts():
    return {"prompts": list_prompt_versions()}


@app.get("/prompts/{version}", tags=["prompts"], dependencies=[Depends(require_internal_key)])
def get_prompt(version: str):
    try:
        text = load_prompt(version)
        return {"version": version, "text": text}
    except Exception as exc:
        raise HTTPException(status_code=404, detail=str(exc))


def _reconcile_criteria_ids(parsed: dict[str, Any], rubric: dict[str, Any]) -> list[str]:
    """Best-effort mapping of model-returned criterion ids onto the rubric.

    Models sometimes return paraphrased/self-invented criterion names while
    scoring perfectly sensibly against the same rubric order. Three passes:
      1. already-valid ids  → untouched
      2. case/space-insensitive relabel
      3. positional remap — only when the model returned exactly as many
         criteria as the rubric defines; each score is capped at that
         criterion's max_score.
    Returns human-readable notes appended to flagged_ambiguities so teachers
    can see the repair happened; empty list = nothing was changed.
    """
    notes: list[str] = []
    rubric_criteria = rubric.get("criteria", [])
    defined_ids = [str(c["id"]) for c in rubric_criteria]
    if not defined_ids:
        return notes
    got = parsed.get("criteria_scores")
    if not isinstance(got, list) or not got:
        return notes

    defined_lower = {d.lower().strip(): d for d in defined_ids}
    unknown = [
        str(c.get("id")) for c in got
        if isinstance(c, dict) and str(c.get("id")) not in defined_ids
    ]
    if not unknown:
        return notes

    # Pass 2: case/whitespace-insensitive relabel.
    still_unknown: list[dict] = []
    for entry in got:
        if not isinstance(entry, dict):
            continue
        cid = str(entry.get("id", ""))
        if cid in defined_ids:
            continue
        match = defined_lower.get(cid.lower().strip())
        if match:
            entry["id"] = match
        else:
            still_unknown.append(entry)

    # Pass 3: positional remap, only for an exact count match.
    if still_unknown and len([e for e in got if isinstance(e, dict)]) == len(defined_ids):
        for entry, rc in zip([e for e in got if isinstance(e, dict)], rubric_criteria):
            old_id = str(entry.get("id"))
            entry["id"] = str(rc["id"])
            cap = float(rc["max_score"])
            try:
                s = float(entry.get("score", 0))
                if s > cap:
                    entry["score"] = cap
            except (TypeError, ValueError):
                pass
            entry["max_score"] = cap
            notes.append(
                f"Criterion id '{old_id}' is not defined in the rubric; "
                f"re-mapped positionally to '{rc['id']}' with max_score {cap:g}."
            )
        return notes

    if still_unknown:
        notes.append(
            "Model returned criterion ids not defined in the rubric: "
            + ", ".join(sorted({str(e.get('id')) for e in still_unknown}))
        )
    return notes


@app.post("/grade", response_model=GradeResponse, tags=["grading"], dependencies=[Depends(require_internal_key)])
async def grade(req: GradeRequest):
    """Grade one handwritten/typed answer. Teacher score is NEVER required or used."""
    t0 = time.time()
    # Deployment-level config only — no per-request provider override.
    provider = settings.MODEL_PROVIDER

    # Convert every attachment (PDF → page PNGs, DOCX → text, PNG/JPG → pass-through).
    for p in [*req.answer_image_paths, *req.question_image_paths]:
        if not Path(p).exists():
            raise HTTPException(status_code=422, detail={
                "stage": "image_load",
                "file": p,
                "error": "Answer file not found",
            })

    try:
        prep = prepare_attachments(req.answer_image_paths, req.question_image_paths)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))

    # A typed DOCX answer legitimately has zero images — its content arrives
    # as extracted text. Only fail when there is nothing to grade at all.
    if not prep.answer_images and not prep.extra_text.strip():
        raise HTTPException(status_code=422, detail="No usable answer files provided")

    # Auditable record of exactly what the grader is about to see — lets you
    # verify "the answer file reached the AI" (and how many pages of it)
    # straight from the econgrader-grading logs in Dozzle.
    logger.info(
        '{"event":"attachments_prepared","answer_images":%d,"question_images":%d,'
        '"answer_text_chars":%d}'
        % (len(prep.answer_images), len(prep.question_images), len(prep.extra_text))
    )

    try:
        grader = get_grader()
    except (ValueError, RuntimeError) as exc:
        raise HTTPException(status_code=422, detail=str(exc))

    rubric_dict = {"criteria": [c.model_dump() for c in req.rubric.criteria]}
    # The typed متن سؤال and the question paper's extracted text are ONE
    # question statement to the AI — either may be empty.
    combined_question_text = prep.combined_question_text(req.question_text)

    def _invoke_grader():
        return grader.grade(
            question_text=combined_question_text,
            question_images=prep.question_images,
            rubric=rubric_dict,
            answer_images=prep.answer_images,
            max_score=req.max_score,
            temperature=req.temperature,
            prompt_version=req.prompt_version,
            extra_text=prep.extra_text,
        )

    def _image_unreadable(result: GradingResult) -> bool:
        """True when the model says it could NOT read the image(s) at all.

        'Handwriting unreadable' is a legitimate grade and must NOT trigger a
        retry — strip those English words before matching. The model is now
        instructed to reply in Persian, so match BOTH languages.
        """
        blob = f"{result.raw_response or ''} {result.reasoning or ''}".lower()
        blob = blob.replace("handwriting", "").replace("handwritten", "")
        markers = (
            # English
            "image could not be read", "image could not be loaded",
            "could not be read or loaded", "could not be loaded",
            "cannot read the image", "no image content", "no readable",
            "nothing in the image", "image is blank", "blank page",
            "was not readable", "could not be read",
            # Persian (the SVG-guidance output language)
            "پاسخی وجود ندارد", "پاسخی موجود نیست", "هیچ پاسخی",
            "هیچ نمودار", "هیچ ترسیمی", "هیچ جمله توضیحی",
            "قابل مشاهده نیست", "قابل مشاهده نمی‌باشد",
            "چیزی در تصویر وجود ندارد", "محتوایی در تصویر",
            "تصویر خالی", "موردی در تصویر", "در تصویر ارسالی وجود ندارد",
            "فقط متن چاپی", "تنها متن چاپی", "متن چاپی سؤال",
        )
        return any(m in blob for m in markers)

    result = _invoke_grader()

    # A parse-type failure means the model returned malformed JSON — transient.
    # An upstream read/connect timeout is equally transient on free-tier
    # gateways whose latency swings wildly between identical calls. Both get
    # one verbatim retry. Budget note: worst case is 2 × upstream timeout plus
    # overhead — keep it below the .NET side's window in Program.cs so a slow
    # model always results in a PERSISTED failed run instead of an HTTP 500.
    if getattr(result, "error_kind", None) in (ERROR_KIND_PARSE, ERROR_KIND_TIMEOUT):
        logger.warning('{"event":"transient_retry","provider":"%s","kind":"%s","attempt":2}'
                       % (provider, result.error_kind))
        result = _invoke_grader()

    # The model occasionally claims the image itself could not be read (a
    # gateway/vision hiccup). When real images were attached AND the model
    # scored zero while claiming nothing was visible, that is not a legitimate
    # grade — retry once verbatim.
    if (
        result.error is None
        and (result.ai_score or 0) <= 0
        and (prep.answer_images or prep.question_images)
        and _image_unreadable(result)
    ):
        logger.warning('{"event":"image_unreadable_retry","provider":"%s","attempt":2}' % provider)
        result = _invoke_grader()

    latency_ms = result.latency_ms or int((time.time() - t0) * 1000)

    validation_errors: list[str] = []
    is_valid = True
    parsed: dict[str, Any] | None = None

    if result.error:
        is_valid = False
        validation_errors = [result.error]
    else:
        parsed, parse_err = parse_json_safe(result.raw_response)
        if parse_err:
            is_valid = False
            validation_errors = [parse_err]
        else:
            # Models occasionally paraphrase criterion ids (e.g. describing the
            # criterion instead of copying "q6_system1_center"). Map whatever
            # they returned onto the real rubric before validating.
            repair_notes = _reconcile_criteria_ids(parsed, rubric_dict)
            if repair_notes:
                for n in repair_notes:
                    logger.warning('{"event":"criteria_id_repair","note":"%s"}' % n)
                parsed.setdefault("flagged_ambiguities", []).extend(repair_notes)
            is_valid, validation_errors = validate_grading_response(
                parsed, result.raw_response, req.max_score, rubric_dict,
            )

    estimated_cost = estimate_cost(
        provider=provider.lower(),
        input_tokens=result.input_tokens,
        output_tokens=result.output_tokens,
        num_images=len(prep.answer_images) + len(prep.question_images),
    )

    resp = GradeResponse(
        run_id=str(uuid.uuid4()),
        provider=result.provider,
        model_name=result.model_name,
        model_version=result.model_version,
        prompt_version=result.prompt_version,
        temperature=result.temperature,
        ai_score=result.ai_score,
        reasoning=result.reasoning,
        criteria_scores=[
            CriterionOut(
                criterion_id=str(c.get("id")),
                score=float(c.get("score", 0)),
                max_score=float(c.get("max_score", 0)),
                comment=c.get("comment"),
            )
            for c in (parsed or {}).get("criteria_scores", [])
            if isinstance(c, dict)
        ] or [
            CriterionOut(criterion_id=c.criterion_id, score=c.score, max_score=c.max_score, comment=c.comment)
            for c in result.criteria_scores
        ],
        confidence=result.confidence,
        flagged_ambiguities=result.flagged_ambiguities,
        is_valid=is_valid and result.error is None,
        validation_errors=[e for e in validation_errors if e],
        raw_response=result.raw_response,
        input_tokens=result.input_tokens,
        output_tokens=result.output_tokens,
        latency_ms=latency_ms,
        estimated_cost_usd=estimated_cost,
        error=result.error,
    )

    _log_request_response(req.model_dump(), resp, result.raw_response, latency_ms)
    return resp


@app.post("/extract", response_model=ExtractionResponse, tags=["extraction"], dependencies=[Depends(require_internal_key)])
async def extract(req: ExtractionRequest):
    """Extract every question + its rubric criteria from ONE exam-wide rubric
    document (the grading key). Saves nothing — the .NET layer shows the result
    as an editable preview; only confirmed rows reach the database."""
    t0 = time.time()
    provider = settings.MODEL_PROVIDER

    for p in req.file_paths:
        if not Path(p).exists():
            raise HTTPException(status_code=422, detail={
                "stage": "image_load",
                "file": p,
                "error": "Rubric document file not found",
            })

    try:
        prep = prepare_attachments([], [], req.file_paths)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))

    # Caps so a huge grading key can't blow the context window — anything
    # beyond the limits is dropped and reported, never silently truncated.
    warnings: list[str] = list(prep.warnings)
    document_images = prep.rubric_images
    if len(document_images) > settings.EXTRACT_MAX_PAGES:
        warnings.append(
            f"Document has {len(document_images)} pages; only the first {settings.EXTRACT_MAX_PAGES} were sent to the AI"
        )
        document_images = document_images[: settings.EXTRACT_MAX_PAGES]
    document_text = prep.rubric_extra_text[: settings.EXTRACT_MAX_TEXT_CHARS]
    if len(prep.rubric_extra_text) > settings.EXTRACT_MAX_TEXT_CHARS:
        warnings.append(
            f"Document text truncated to {settings.EXTRACT_MAX_TEXT_CHARS} characters"
        )

    try:
        grader = get_grader()
    except (ValueError, RuntimeError) as exc:
        raise HTTPException(status_code=422, detail=str(exc))

    def _invoke_extractor():
        return grader.extract(
            document_text=document_text,
            document_images=document_images,
            document_name=req.document_name or "rubric document",
            temperature=req.temperature,
            prompt_version=req.prompt_version,
        )

    result = _invoke_extractor()

    # Same transient-retry contract as /grade: malformed JSON and upstream
    # timeouts are retried once verbatim. Budget: one 280s timeout plus a fast
    # parse retry stays inside the .NET side's 315s attempt window.
    if getattr(result, "error_kind", None) in (ERROR_KIND_PARSE, ERROR_KIND_TIMEOUT):
        logger.warning('{"event":"transient_retry","provider":"%s","kind":"%s","attempt":2,"stage":"extraction"}'
                       % (provider, result.error_kind))
        result = _invoke_extractor()

    latency_ms = result.latency_ms or int((time.time() - t0) * 1000)

    is_valid, validation_errors = False, [result.error] if result.error else []
    parsed = result.parsed
    questions: list[ExtractedQuestionOut] = []

    if result.error:
        is_valid = False
        validation_errors = [result.error]
    else:
        if parsed is None:
            parsed, parse_err = parse_json_safe(result.raw_response)
        else:
            parse_err = None
        if parse_err:
            is_valid = False
            validation_errors = [parse_err]
        else:
            is_valid, validation_errors, rows, extract_warnings = validate_extraction(parsed, result.raw_response)
            warnings.extend(extract_warnings)
            questions = [
                ExtractedQuestionOut(
                    number=row["number"],
                    text=row["text"],
                    max_score=row["max_score"],
                    criteria=[ExtractedCriterionOut(**c) for c in row["criteria"]],
                )
                for row in rows
            ]

    estimated_cost = estimate_cost(
        provider=provider.lower(),
        input_tokens=result.input_tokens,
        output_tokens=result.output_tokens,
        num_images=len(document_images),
    )

    resp = ExtractionResponse(
        provider=result.provider,
        model_name=result.model_name,
        model_version=result.model_version,
        prompt_version=result.prompt_version,
        questions=questions,
        warnings=warnings,
        is_valid=is_valid and result.error is None,
        validation_errors=[e for e in validation_errors if e],
        raw_response=result.raw_response,
        input_tokens=result.input_tokens,
        output_tokens=result.output_tokens,
        latency_ms=latency_ms,
        estimated_cost_usd=estimated_cost,
        error=result.error,
    )

    logger.info(
        '{"event":"extraction","provider":"%s","model":"%s","prompt_version":"%s",'
        '"pages":%d,"text_chars":%d,"questions":%d,"is_valid":%s,"latency_ms":%d,'
        '"input_tokens":%d,"output_tokens":%d,"cost_usd":%.6f,"error":%s}'
        % (result.provider, result.model_name, result.prompt_version,
           len(document_images), len(document_text), len(questions), is_valid, latency_ms,
           result.input_tokens, result.output_tokens, estimated_cost,
           json.dumps(result.error))
    )
    return resp


@app.post("/evaluate", tags=["evaluation"], dependencies=[Depends(require_internal_key)])
def evaluate(req: EvaluationRequest):
    pairs = [(r.get("teacher_score"), r.get("ai_score")) for r in req.runs]
    filtered = [(float(t), float(a)) for t, a in pairs if t is not None and a is not None]
    if not filtered:
        raise HTTPException(status_code=422, detail="No runs with both teacher_score and ai_score found")
    if len(filtered) < len(req.runs):
        logger.warning('{"event":"evaluate_skip","skipped":%d,"total":%d}' % (len(req.runs) - len(filtered), len(req.runs)))

    ts = [t for t, _ in filtered]
    ai = [a for _, a in filtered]
    m = compute_metrics(ts, ai)
    return m.__dict__


@app.post("/evaluate/by-provider", tags=["evaluation"], dependencies=[Depends(require_internal_key)])
def evaluate_by_provider(req: EvaluationRequest):
    return {k: v.__dict__ for k, v in aggregate_by_provider(req.runs).items()}


@app.exception_handler(Exception)
async def global_exception_handler(request: Request, exc: Exception):
    logger.exception('{"event":"unhandled_error","path":"%s"}' % request.url.path)
    return JSONResponse(status_code=500, content={
        "error": "Internal error in grading service",
        "path": str(request.url.path),
        "detail": str(exc),
    })