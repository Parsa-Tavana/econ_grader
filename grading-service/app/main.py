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
from .schemas import GradeRequest, GradeResponse, CriterionOut, HealthResponse, EvaluationRequest
from .validation import validate_grading_response, parse_json_safe
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
        "input_tokens": resp.input_tokens if resp else 0,
        "output_tokens": resp.output_tokens if resp else 0,
        "cost_usd": resp.estimated_cost_usd if resp else 0,
        "raw_len": len(raw_text or ""),
    }
    logger.info(json.dumps(record))


@app.get("/health", response_model=HealthResponse, tags=["ops"])
def health():
    return HealthResponse(
        status="ok",
        provider=settings.MODEL_PROVIDER,
        model_name=settings.MODEL_NAME,
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


@app.post("/grade", response_model=GradeResponse, tags=["grading"], dependencies=[Depends(require_internal_key)])
async def grade(req: GradeRequest):
    """Grade one handwritten/typed answer. Teacher score is NEVER required or used."""
    t0 = time.time()
    provider = req.provider or settings.MODEL_PROVIDER

    # Convert every attachment (PDF → page PNGs, DOCX → text, PNG/JPG → pass-through).
    for p in [*req.answer_image_paths, *req.question_image_paths, *req.rubric_file_paths]:
        if not Path(p).exists():
            raise HTTPException(status_code=422, detail={
                "stage": "image_load",
                "file": p,
                "error": "Answer file not found",
            })

    try:
        prep = prepare_attachments(req.answer_image_paths, req.question_image_paths, req.rubric_file_paths)
    except ValueError as exc:
        raise HTTPException(status_code=422, detail=str(exc))

    # A typed DOCX answer legitimately has zero images — its content arrives
    # as extracted text. Only fail when there is nothing to grade at all.
    if not prep.answer_images and not prep.extra_text.strip():
        raise HTTPException(status_code=422, detail="No usable answer files provided")

    try:
        grader = get_grader(provider)
    except (ValueError, RuntimeError) as exc:
        raise HTTPException(status_code=422, detail=str(exc))

    rubric_dict = {"criteria": [c.model_dump() for c in req.rubric.criteria]}
    # The typed متن سؤال and the question paper's extracted text are ONE
    # question statement to the AI — either may be empty.
    combined_question_text = prep.combined_question_text(req.question_text)
    result = grader.grade(
        question_text=combined_question_text,
        question_images=prep.question_images,
        rubric=rubric_dict,
        answer_images=prep.answer_images,
        max_score=req.max_score,
        temperature=req.temperature,
        prompt_version=req.prompt_version,
        extra_text=prep.extra_text,
        rubric_text=prep.rubric_extra_text,
        rubric_images=prep.rubric_images,
    )

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
            is_valid, validation_errors = validate_grading_response(
                parsed, result.raw_response, req.max_score, rubric_dict
            )

    estimated_cost = estimate_cost(
        provider=provider.lower(),
        input_tokens=result.input_tokens,
        output_tokens=result.output_tokens,
        num_images=len(prep.answer_images) + len(prep.question_images) + len(prep.rubric_images),
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