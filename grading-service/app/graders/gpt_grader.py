"""GptVisionGrader — GPT slot on an OpenAI-compatible gateway (GPT_* settings)."""
from ..config import settings
from .qwen_grader import OpenAICompatibleGrader


class GptVisionGrader(OpenAICompatibleGrader):
    """GPT slot — OpenAI-compatible gateway configured via the GPT_* settings."""

    def __init__(self) -> None:
        super().__init__(
            provider_label="gpt",
            base_url=settings.GPT_BASE_URL,
            api_key=settings.GPT_API_KEY,
            model=settings.GPT_MODEL or settings.MODEL_NAME,
            auth_scheme=settings.GPT_AUTH_SCHEME,
        )
