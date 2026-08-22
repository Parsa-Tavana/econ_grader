"""Cost estimation — pricing lives in external config, never hard-coded."""
from __future__ import annotations

from dataclasses import dataclass
from typing import Optional
import json
import logging
from pathlib import Path

logger = logging.getLogger(__name__)

# Fallback prices used if pricing.json is absent — update pricing.json instead of editing here.
_FALLBACK = {
    "claude": {"input_per_million": 3.0, "output_per_million": 15.0, "image_per_million": 0.0},
    "gemini": {"input_per_million": 1.5, "output_per_million": 6.0, "image_per_million": 0.0},
    "qwen":   {"input_per_million": 0.0, "output_per_million": 0.0, "image_per_million": 0.0},
}


def _load_pricing(path: str | None = None) -> dict:
    for p in (path, "pricing.json", "app/pricing.json", "../pricing.json"):
        if p and Path(p).exists():
            try:
                return json.loads(Path(p).read_text(encoding="utf-8"))
            except Exception:
                logger.warning("Failed to load pricing file %s", p)
    logger.info("Using fallback cost pricing (pricing.json not found)")
    return _FALLBACK


def estimate_cost(
    *,
    provider: str,
    input_tokens: int,
    output_tokens: int,
    num_images: int = 0,
    pricing_path: Optional[str] = None,
) -> float:
    """Cost in USD for one grading call."""
    pricing = _load_pricing(pricing_path)
    cfg = pricing.get(provider.lower(), pricing.get("default", _FALLBACK.get(provider.lower(), {})))
    
    ip = float(cfg.get("input_per_million", 0) or 0)
    op = float(cfg.get("output_per_million", 0) or 0)
    
    # Self-hosted providers (is_self_hosted=True) have zero token cost
    if cfg.get("is_self_hosted", False):
        return 0.0

    cost = (input_tokens / 1_000_000) * ip + (output_tokens / 1_000_000) * op
    # Images billed as extra input tokens on most providers; keep hook here
    image_price = float(cfg.get("image_per_million", 0) or 0)
    cost += (num_images / 1_000) * image_price  # per-image pricing if present
    return round(cost, 6)