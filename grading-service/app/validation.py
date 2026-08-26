"""Strict response validation — never silently default or hide a bad score.

Every AI response is checked:
  - valid JSON
  - score within [0, max_score]
  - each criterion score within its own max_score
  - sum of criteria scores <= max_score

Any failure -> is_valid=False + validation_errors[]. The caller (and DB)
records the failure explicitly.
"""
from __future__ import annotations

import json
import logging
from typing import Any

logger = logging.getLogger(__name__)


def validate_grading_response(
    parsed: dict[str, Any] | None,
    raw_text: str,
    max_score: float,
    rubric: dict[str, Any],
) -> tuple[bool, list[str]]:
    """Return (is_valid, validation_errors)."""
    errors: list[str] = []

    if parsed is None:
        return False, ["Response is not valid JSON"]

    if not isinstance(parsed, dict):
        return False, ["Parsed JSON is not an object"]

    # score field
    if "score" not in parsed:
        errors.append("Missing required field 'score'")
    else:
        try:
            s = float(parsed["score"])
        except (TypeError, ValueError):
            errors.append(f"'score' is not numeric: {parsed['score']!r}")
        else:
            if s < 0 or s > max_score:
                errors.append(f"'score' {s} outside allowed range [0, {max_score}]")

    # reasoning field
    if "reasoning" not in parsed or not isinstance(parsed["reasoning"], str):
        errors.append("Missing or non-string 'reasoning'")

    # criteria_scores
    criteria = rubric.get("criteria", [])
    expected_ids = {c["id"] for c in criteria}
    max_by_id = {c["id"]: float(c["max_score"]) for c in criteria}

    if "criteria_scores" not in parsed:
        errors.append("Missing required field 'criteria_scores'")
    elif not isinstance(parsed["criteria_scores"], list):
        errors.append("'criteria_scores' is not an array")
    else:
        seen_ids: set[str] = set()
        crit_sum = 0.0
        for c in parsed["criteria_scores"]:
            if not isinstance(c, dict) or "id" not in c:
                errors.append(f"Malformed criteria entry: {c!r}")
                continue
            cid = str(c["id"])
            seen_ids.add(cid)
            if cid not in max_by_id:
                errors.append(f"Criterion '{cid}' is not defined in the rubric")
                continue
            try:
                cs = float(c["score"])
            except (TypeError, ValueError):
                errors.append(f"Criterion '{cid}' score is not numeric: {c.get('score')!r}")
                continue
            cmx = max_by_id[cid]
            if cs < 0 or cs > cmx:
                errors.append(f"Criterion '{cid}' score {cs} outside [0, {cmx}]")
            crit_sum += cs
        missing = expected_ids - seen_ids
        if missing:
            errors.append(f"Rubric criteria missing from response: {sorted(missing)}")
        if crit_sum > max_score + 1e-9:
            errors.append(f"Sum of criteria scores {crit_sum} exceeds total max_score {max_score}")

    # confidence
    if "confidence" in parsed and parsed["confidence"] is not None:
        try:
            cf = float(parsed["confidence"])
            if cf < 0 or cf > 1:
                errors.append(f"'confidence' {cf} outside [0,1]")
        except (TypeError, ValueError):
            errors.append(f"'confidence' is not numeric: {parsed['confidence']!r}")

    return len(errors) == 0, errors


def parse_json_safe(raw_text: str) -> tuple[dict[str, Any] | None, str | None]:
    """Try to parse JSON from a model response. Returns (parsed, error).

    Delegates to the hardened parser (fence stripping, brace extraction,
    trailing-comma and unescaped-quote repair, json-repair fallback) so a
    transient model-output glitch never fails an otherwise-good run.
    """
    from .json_repair_util import parse_json_hardened
    return parse_json_hardened(raw_text)