"""Pydantic schemas for the internal API — the contract between .NET and Python."""
from __future__ import annotations

from typing import Any, Optional
from pydantic import BaseModel, Field


class CriterionIn(BaseModel):
    id: str
    description: str
    max_score: float


class RubricIn(BaseModel):
    criteria: list[CriterionIn] = Field(default_factory=list)


class GradeRequest(BaseModel):
    student_id: str = Field(..., description="External student identifier")
    question_id: str = Field(..., description="Question identifier")
    question_text: str = Field(..., description="Typed question text; may be empty when the question arrives only as a document (merged with its extracted text)")
    rubric: RubricIn
    answer_image_paths: list[str] = Field(..., description="Paths to PNG files on disk (already extracted)")
    question_image_paths: list[str] = Field(default_factory=list, description="Question paper files (PDF/PNG/JPG/DOCX/XLSX/XLS) — text content is treated as part of the question statement")
    rubric_file_paths: list[str] = Field(default_factory=list, description="Rubric document files, routed to the AI as rubric material rather than question material")
    max_score: float
    temperature: float = 0.0
    prompt_version: str = "default"
    provider: Optional[str] = Field(default=None, description="Override MODEL_PROVIDER for this run")


class CriterionOut(BaseModel):
    criterion_id: str
    score: float
    max_score: float
    comment: Optional[str] = None


class GradeResponse(BaseModel):
    run_id: str
    provider: str
    model_name: str
    model_version: Optional[str]
    prompt_version: str
    temperature: float
    
    ai_score: float
    reasoning: str
    criteria_scores: list[CriterionOut]
    confidence: Optional[float]
    flagged_ambiguities: list[str]
    
    is_valid: bool
    validation_errors: list[str]
    
    raw_response: str
    input_tokens: int
    output_tokens: int
    latency_ms: int
    estimated_cost_usd: float
    error: Optional[str]


class HealthResponse(BaseModel):
    status: str
    provider: str
    model_name: str
    prompts_available: list[str]


class EvaluationRequest(BaseModel):
    """Request to compute evaluation metrics over a set of runs."""
    runs: list[dict[str, Any]]  # each: {ai_score, teacher_score}
