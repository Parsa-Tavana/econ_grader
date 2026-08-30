"""GlmVisionGrader — GLM slot on an OpenAI-compatible gateway (GLM_* settings).

This is the DEFAULT grading provider (MODEL_PROVIDER=glm).
"""
from ..config import settings
from .openai_compatible import OpenAICompatibleGrader


class GlmVisionGrader(OpenAICompatibleGrader):
    """GLM slot — OpenAI-compatible gateway configured via the GLM_* settings."""

    def __init__(self) -> None:
        super().__init__(
            provider_label="glm",
            base_url=settings.GLM_BASE_URL,
            api_key=settings.GLM_API_KEY,
            model=settings.GLM_MODEL or settings.MODEL_NAME,
            auth_scheme=settings.GLM_AUTH_SCHEME,
        )