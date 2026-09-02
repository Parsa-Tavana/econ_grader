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
    MODEL_NAME: str = Field(default="GLM-5.3")
    MODEL_VERSION: Optional[str] = None

    # API keys (from env/secrets)
    # First slot — GLM platform endpoint (default provider). OpenAI-compatible
    # /chat/completions with "apikey" auth (NOT Bearer).
    GLM_BASE_URL: str = Field(default="", description="GLM endpoint WITHOUT /chat/completions")
    GLM_API_KEY: str = Field(default="not-needed")
    GLM_MODEL: str = Field(default="GLM-5.3")
    GLM_AUTH_SCHEME: str = Field(default="apikey", description="Authorization header prefix: Bearer | apikey")
    # Second slot — GPT-5.6-Sol, reserved for future use (MODEL_PROVIDER=gpt).
    GPT_BASE_URL: str = Field(default="", description="GPT endpoint WITHOUT /chat/completions")
    GPT_API_KEY: str = Field(default="not-needed")
    GPT_MODEL: str = Field(default="GPT-5.6-Sol")
    GPT_AUTH_SCHEME: str = Field(default="apikey", description="Authorization header prefix: Bearer | apikey")
    
    # Default grading parameters
    DEFAULT_TEMPERATURE: float = 0.0
    DEFAULT_MAX_TOKENS: int = 2048

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

    class Config:
        env_file = ".env"
        env_file_encoding = "utf-8"


settings = Settings()