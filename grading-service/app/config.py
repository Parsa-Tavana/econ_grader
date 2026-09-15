from pydantic_settings import BaseSettings
from pydantic import Field
from typing import Optional
import json


class Settings(BaseSettings):
    # Provider selection — the slot used for every grading run. The user
    # cannot choose per run; this is deployment-level config only.
    MODEL_PROVIDER: str = Field(default="glm", description="glm | gpt")
    # Per-provider models — each grader uses its own; MODEL_NAME stays as the
    # legacy fallback so existing deployments keep working.
    MODEL_NAME: str = Field(default="GLM-5.3-Flash")
    MODEL_VERSION: Optional[str] = None

    # API keys (from env/secrets)
    # First slot — GLM platform endpoint (default provider). OpenAI-compatible
    # /chat/completions with "apikey" auth (NOT Bearer).
    GLM_BASE_URL: str = Field(default="https://api.arvancloudai.ir/v1", description="GLM endpoint WITHOUT /chat/completions")
    GLM_API_KEY: str = Field(default="not-needed")
    GLM_MODEL: str = Field(default="GLM-5.3-Flash")
    GLM_AUTH_SCHEME: str = Field(default="Bearer", description="Authorization header prefix: Bearer | apikey")
    # Stream /chat/completions (SSE). Default on — gateways with idle timeouts
    # (ArvanCloud cuts silent requests at ~60s) kill long vision calls.
    GLM_STREAMING: bool = Field(default=True)
    # Second slot — GPT-5.6-Sol, reserved for future use (MODEL_PROVIDER=gpt).
    GPT_BASE_URL: str = Field(default="", description="GPT endpoint WITHOUT /chat/completions")
    GPT_API_KEY: str = Field(default="not-needed")
    GPT_MODEL: str = Field(default="GPT-5.6-Sol")
    GPT_AUTH_SCHEME: str = Field(default="apikey", description="Authorization header prefix: Bearer | apikey")
    GPT_STREAMING: bool = Field(default=True)
    
    # Default grading parameters
    DEFAULT_TEMPERATURE: float = 0.0
    DEFAULT_MAX_TOKENS: int = 2048
    # M5: ask the gateway for JSON mode (response_format={"type":"json_object"}).
    # Cuts parse failures at the source; the hardened parser stays as fallback.
    # Turn off (0) only for a gateway that rejects the parameter outright.
    RESPONSE_FORMAT_JSON_OBJECT: bool = True

    # Exam-rubric extraction (/extract): a whole grading key must come back as
    # ONE JSON document, so it needs a much larger token budget than a single
    # grading run. Timeout must stay below the .NET side's 315s attempt window
    # (worst case: this timeout + one verbatim retry would exceed it — the
    # endpoint retries only parse/timeout-tagged failures, and one 280s timeout
    # plus a fast parse-failure retry still fits).
    EXTRACTION_MAX_TOKENS: int = 16384
    EXTRACTION_TIMEOUT_SECONDS: float = 280.0
    # Caps so a 200-page grading key can't blow the context window; anything
    # beyond is dropped with an explicit warning in the response.
    EXTRACT_MAX_PAGES: int = 20
    EXTRACT_MAX_TEXT_CHARS: int = 150_000

    # Ingest (/ingest): JPEG quality for the compact 150 DPI page format the
    # .NET side dedups by content hash. The legacy 200 DPI PNG is always
    # rendered too so the golden-set harness can A/B formats before JPEG
    # becomes the grading default (foundation plan M1/M4).
    INGEST_JPEG_QUALITY: int = 85
    # Per-document page cap at ingest (mirrors Ingest:MaxPages on the .NET side).
    INGEST_MAX_PAGES: int = 20

    # M6 bulk split (/split-header): top % of each page treated as the header
    # band where students write their student number. Mirrors Split:HeaderBandPct
    # on the .NET side.
    SPLIT_HEADER_BAND_PCT: float = 12.0
    # Echoed default for the confidence threshold; the actual accept/reject
    # decision (never auto-assign below threshold) lives on the .NET side.
    SPLIT_OCR_CONFIDENCE_THRESHOLD: float = 0.8

    # Storage
    IMAGE_STORAGE_ROOT: str = Field(default="storage/images")
    PROMPTS_DIR: str = Field(default="app/prompts")
    
    # Logging
    LOG_LEVEL: str = "INFO"

    # Internal service auth: the .NET API sends X-Internal-Key on every call.
    # Empty key + ENVIRONMENT=production → protected endpoints reject ALL
    # requests (fail-closed). In non-production (local bare-metal dev) an empty
    # key disables the check entirely so `uvicorn app.main:app` still works.
    GRADING_INTERNAL_KEY: str = ""
    ENVIRONMENT: str = "development"

    # Langfuse tracing (optional). Unset keys → tracing disabled, zero effect
    # on grading. HOST defaults to the compose-network service name; set the
    # public https URL when calling Langfuse from outside Docker.
    LANGFUSE_PUBLIC_KEY: str = ""
    LANGFUSE_SECRET_KEY: str = ""
    LANGFUSE_HOST: str = "http://langfuse:3000"

    class Config:
        env_file = ".env"
        env_file_encoding = "utf-8"


settings = Settings()