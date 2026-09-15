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
    "glm":     {"input_per_million": 0.0, "output_per_million": 0.0, "image_per_million": 0.0},
    "gpt":     {"input_per_million": 0.0, "output_per_million": 0.0, "image_per_million": 0.0},
    "default": {"input_per_million": 0.0, "output_per_million": 0.0, "image_per_million": 0.0},
}

# M5 payload hygiene: pricing.json is read from disk on EVERY /grade and
# /extract call today. Cache it in-process, keyed by (path, mtime) so an edit
# to pricing.json is picked up on the next call without a restart (cheap
# file-watch semantics — no watcher thread needed).


def _load_pricing_cached(path: str | None) -> dict:
    candidates: list[str] = [path] if path else ["pricing.json", "app/pricing.json", "../pricing.json"]
    for p in candidates:
        f = Path(p) if p else None
        if f and f.exists():
            try:
                mtime = f.stat().st_mtime
            except OSError:
                continue
            cached = _PRICING_CACHE.get(p)
            if cached is not None and cached[0] == mtime:
                return cached[1]
            try:
                data = json.loads(f.read_text(encoding="utf-8"))
            except Exception:
                logger.warning("Failed to load pricing file %s", p)
                continue
            _PRICING_CACHE[p] = (mtime, data)
            return data
    logger.info("Using fallback cost pricing (pricing.json not found)")
    return _FALLBACK


_PRICING_CACHE: dict[str, tuple[float, dict]] = {}


def _load_pricing(path: str | None = None) -> dict:
    return _load_pricing_cached(path)


def estimate_cost(
    *,
    provider: str,
    input_tokens: int,
    output_tokens: int,
    num_images: int = 0,
    pricing_path: Optional[str] = None,
) -> float:
    """Cost in USD for one grading call."""
    pricing = _load_pricing_cached(pricing_path)
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