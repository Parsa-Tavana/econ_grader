from pydantic_settings import BaseSettings
from pydantic import Field
from typing import Optional
import json


class Settings(BaseSettings):
    # Provider selection
    MODEL_PROVIDER: str = Field(default="claude", description="claude | gemini | qwen")
    # Per-provider models — each grader uses its own; MODEL_NAME stays as the
    # legacy fallback so existing deployments keep working.
    CLAUDE_MODEL: str = Field(default="claude-3-5-sonnet-20241022")
    GEMINI_MODEL: str = Field(default="gemini-2.0-flash")
    QWEN_MODEL: str = Field(default="qwen2.5-vl-7b-instruct")
    MODEL_NAME: str = Field(default="claude-3-5-sonnet-20241022")
    MODEL_VERSION: Optional[str] = None
    
    # API keys (from env/secrets)
    ANTHROPIC_API_KEY: Optional[str] = None
    GOOGLE_API_KEY: Optional[str] = None
    QWEN_BASE_URL: str = Field(default="http://localhost:8000/v1", description="OpenAI-compatible endpoint for self-hosted Qwen")
    QWEN_API_KEY: str = Field(default="not-needed")
    
    # Default grading parameters
    DEFAULT_TEMPERATURE: float = 0.0
    DEFAULT_MAX_TOKENS: int = 2048
    
    # Storage
    IMAGE_STORAGE_ROOT: str = Field(default="storage/images")
    PROMPTS_DIR: str = Field(default="app/prompts")
    
    # Logging
    LOG_LEVEL: str = "INFO"

    # Langfuse LLM tracing (optional). When LANGFUSE_PUBLIC_KEY/SECRET_KEY are
    # set, every Claude call is traced to a self-hosted Langfuse instance —
    # prompt, response, tokens, latency, cost. Empty keys = fully disabled.
    LANGFUSE_PUBLIC_KEY: Optional[str] = None
    LANGFUSE_SECRET_KEY: Optional[str] = None
    LANGFUSE_HOST: str = "http://langfuse:3000"

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