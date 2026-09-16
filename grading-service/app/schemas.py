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
    max_score: float
    temperature: float = 0.0
    prompt_version: str = "default"


class CriterionOut(BaseModel):
    criterion_id: str
    score: float
    max_score: float
    comment: Optional[str] = None


class ExtractionRequest(BaseModel):
    """Ask the AI to extract every question + its rubric criteria from one
    exam-wide rubric/grading-key document."""
    file_paths: list[str] = Field(..., description="Absolute paths of the exam rubric document (PDF/PNG/JPG/DOCX/XLSX/XLS)")
    document_name: Optional[str] = Field(default=None, description="Original file name, shown to the model for context")
    temperature: float = 0.0
    prompt_version: str = "extract"


class ExtractedCriterionOut(BaseModel):
    id: str
    description: str
    max_score: float


class ExtractedQuestionOut(BaseModel):
    number: int
    text: str
    max_score: float
    criteria: list[ExtractedCriterionOut] = Field(default_factory=list)


class ExtractionResponse(BaseModel):
    provider: str
    model_name: str
    model_version: Optional[str] = None
    prompt_version: str
    questions: list[ExtractedQuestionOut] = Field(default_factory=list)
    warnings: list[str] = Field(default_factory=list)
    is_valid: bool
    validation_errors: list[str] = Field(default_factory=list)
    raw_response: str
    input_tokens: int = 0
    output_tokens: int = 0
    latency_ms: int = 0
    estimated_cost_usd: float = 0.0
    error: Optional[str] = None


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


class IngestFormatOut(BaseModel):
    """One rendered form of a page: content hash + base64 payload.

    sha256 is the hash of THIS payload's bytes (not the parent document) —
    that is what lets the .NET side dedup identical rendered pages across
    different documents by content address alone.
    """
    format: str  # png200 | jpeg150
    sha256: str
    media_type: str
    data_b64: str


class IngestPageOut(BaseModel):
    page_number: int
    width: int
    height: int
    formats: list[IngestFormatOut]


class IngestRequest(BaseModel):
    """Render/normalize ONE uploaded document at ingest time (M1 pipeline)."""
    path: str = Field(..., description="Absolute path on the shared storage volume")
    formats: list[str] = Field(default_factory=lambda: ["png200", "jpeg150"],
                               description="Which page formats to render: png200 and/or jpeg150")
    max_pages: Optional[int] = Field(default=None, description="Page cap; defaults to EXTRACT_MAX_PAGES")
    page_offset: int = Field(default=0, ge=0, description="Skip the first N pages (M6 bulk-split windowing: big merged PDFs are ingested in slices); response page numbers remain source-document positions")


class IngestResponse(BaseModel):
    pages: list[IngestPageOut] = Field(default_factory=list)
    text: str = ""              # extracted DOCX/XLSX text ("" for PDFs/images)
    total_pages: int = 0        # total pages in the SOURCE document (0 for text-only docs)
    warnings: list[str] = Field(default_factory=list)


class SplitHeaderRequest(BaseModel):
    """M6 bulk split: OCR the header band of already-ingested answer pages."""
    page_paths: list[str] = Field(..., description="Absolute paths of page images (PNG/JPG) on the shared volume, in page order")
    header_band_pct: Optional[float] = Field(default=None, description="Top % of the page treated as the header band; defaults to SPLIT_HEADER_BAND_PCT")


class SplitHeaderPageOut(BaseModel):
    """Per-page OCR facts — no grouping here; the .NET layer groups."""
    page_number: int
    raw_id: Optional[str] = None      # normalized student id, None = unreadable
    confidence: float = 0.0           # 0..1, from tesseract word confidences
    ocr_text: str = ""                # raw header text (auditable, truncated)


class SplitHeaderResponse(BaseModel):
    pages: list[SplitHeaderPageOut] = Field(default_factory=list)
    warnings: list[str] = Field(default_factory=list)
