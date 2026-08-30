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
    allow_unlisted_criteria: bool = False,
) -> tuple[bool, list[str]]:
    """Return (is_valid, validation_errors).

    `allow_unlisted_criteria=True` is for rubrics whose criteria live entirely
    in an attached document (Excel/PDF/DOCX) rather than structured rows: the
    model returns criterion ids drawn from that document, which are unknown to
    the structured rubric. We then validate each score against the rubric's
    total max_score (and the AI's own per-criterion max_score, if provided)
    instead of rejecting them as undefined.
    """
    errors: list[str] = []

    if parsed is None:
        return False, ["Response is not valid JSON"]

    if not isinstance(parsed, dict):
        return False, ["Parsed JSON is not an object"]

    # score field
    score_val: float | None = None
    if "score" not in parsed:
        errors.append("Missing required field 'score'")
    else:
        try:
            score_val = float(parsed["score"])
        except (TypeError, ValueError):
            errors.append(f"'score' is not numeric: {parsed['score']!r}")
        else:
            if score_val < 0 or score_val > max_score:
                errors.append(f"'score' {score_val} outside allowed range [0, {max_score}]")

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
            cmx = max_by_id.get(cid)
            if cmx is None:
                if not allow_unlisted_criteria:
                    errors.append(f"Criterion '{cid}' is not defined in the rubric")
                    continue
                # Document-sourced rubric: no declared per-criterion max in the
                # structured rubric, so bound by the AI's own per-criterion
                # max_score if given, otherwise by the overall max_score.
                try:
                    cmx = float(c.get("max_score", max_score))
                except (TypeError, ValueError):
                    cmx = max_score
                if cmx < 0:
                    cmx = max_score
            try:
                cs = float(c["score"])
            except (TypeError, ValueError):
                errors.append(f"Criterion '{cid}' score is not numeric: {c.get('score')!r}")
                continue
            if cs < 0 or cs > cmx:
                errors.append(f"Criterion '{cid}' score {cs} outside [0, {cmx}]")
            # The AI echoing its own per-criterion max is fine, but it must
            # never EXCEED the rubric's declared max (a hallucination symptom:
            # a bigger max quietly legitimizes inflated scores).
            if cmx is not None and c.get("max_score") is not None:
                try:
                    declared = float(c["max_score"])
                except (TypeError, ValueError):
                    declared = None
                if declared is not None and declared > cmx + 1e-9:
                    errors.append(
                        f"Criterion '{cid}' declares max_score {declared} exceeding the rubric's {cmx}"
                    )
            crit_sum += cs
        missing = expected_ids - seen_ids
        if missing:
            errors.append(f"Rubric criteria missing from response: {sorted(missing)}")
        if crit_sum > max_score + 1e-9:
            errors.append(f"Sum of criteria scores {crit_sum} exceeds total max_score {max_score}")
        # Consistency: the headline score MUST be the sum of the criteria.
        # Without this check a model could report a generous "score" that its
        # own per-criterion marks do not add up to, and it would pass as valid.
        if score_val is not None and abs(score_val - crit_sum) > 0.05:
            errors.append(
                f"Total 'score' {score_val} does not match the sum of criteria scores {crit_sum}"
            )

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