"""Load versioned prompt templates from the filesystem.

Prompts live as versioned template files: question6_v1.txt, question6_v2.txt, etc.
Every grading run records the prompt_version so results are comparable across
prompt iterations without losing history.
"""
from __future__ import annotations

import os
import logging
from pathlib import Path

from ..config import settings

logger = logging.getLogger(__name__)


def load_prompt(prompt_version: str) -> str:
    """Load prompt template by version (e.g. 'question6_v1', 'v2', 'default')."""
    # Try exact filename first
    for ext in (".txt", ".md", ".prompt"):
        p = Path(settings.PROMPTS_DIR) / f"{prompt_version}{ext}"
        if p.exists():
            return p.read_text(encoding="utf-8")
    
    # Fallback: search by prefix
    matches = list(Path(settings.PROMPTS_DIR).glob(f"{prompt_version}*"))
    if matches:
        return matches[0].read_text(encoding="utf-8")

    # Fallback to default
    default = Path(settings.PROMPTS_DIR) / "default.txt"
    if default.exists():
        logger.warning("Prompt '%s' not found, falling back to default.txt", prompt_version)
        return default.read_text(encoding="utf-8")

    # Hard-coded fallback (last resort)
    logger.warning("No prompt files found at all — using built-in fallback")
    return (
        "You are grading a handwritten Economics Olympiad answer.\n\n"
        "Question: {question_text}\n\n"
        "Rubric (criteria with max scores):\n{rubric_json}\n\n"
        "Maximum total score: {max_score}\n\n"
        "Examine the attached question images (the printed question) and the "
        "student's handwritten answer images. Grade the answer according to the "
        "rubric. Be strict but fair.\n\n"
        "Return ONLY valid JSON with these fields:\n"
        "- \"score\": number (0 to max_score)\n"
        "- \"reasoning\": string (concise explanation)\n"
        "- \"criteria_scores\": array of objects with keys "
        "\"id\", \"score\", \"max_score\", \"comment\"\n"
        "- \"confidence\": number 0-1\n"
        "- \"flagged_ambiguities\": array of strings\n\n"
        "Do NOT include markdown fences or extra commentary."
    )


def list_prompt_versions() -> list[str]:
    """Return available prompt version identifiers."""
    versions = []
    for ext in (".txt", ".md", ".prompt"):
        for p in Path(settings.PROMPTS_DIR).glob(f"*{ext}"):
            versions.append(p.stem)
    return sorted(set(versions))