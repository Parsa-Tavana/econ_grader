from pydantic_settings import BaseSettings
from pydantic import Field
from typing import Optional
import json


class Settings(BaseSettings):
    # Provider selection
    MODEL_PROVIDER: str = Field(default="claude", description="claude | gemini | qwen")
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
    
    class Config:
        env_file = ".env"
        env_file_encoding = "utf-8"


settings = Settings()