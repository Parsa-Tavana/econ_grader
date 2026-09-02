"""Validation for exam-wide rubric EXTRACTION responses.

Separate from validation.py on purpose: a grading response must be strictly
valid (score sums, coverage) because it directly produces marks, while an
extraction response is a PROPOSAL the teacher edits in the preview dialog
before anything is saved. So extraction validation is tolerant: it drops or
warns instead of failing whenever the teacher can reasonably fix the row in
the preview.
"""
from __future__ import annotations

import logging
from typing import Any

logger = logging.getLogger(__name__)

MAX_QUESTION_TEXT_CHARS = 8000
MAX_CRITERION_DESC_CHARS = 2000


def validate_extraction(
    parsed: dict[str, Any] | None,
    raw_text: str,
) -> tuple[bool, list[str], list[dict[str, Any]], list[str]]:
    """Validate an extraction response. Returns (is_valid, errors, questions, warnings).

    Only whole-batch problems (unparseable JSON, no questions at all) mark the
    response invalid — the caller surfaces them as a hard error. Individual
    bad rows are dropped with a warning instead, so one glitched question
    doesn't discard the whole extraction.
    """
    errors: list[str] = []
    warnings: list[str] = []

    if parsed is None:
        return False, ["Response is not valid JSON"], [], []
    if not isinstance(parsed, dict):
        return False, ["Parsed JSON is not an object"], [], []

    raw_questions = parsed.get("questions")
    if not isinstance(raw_questions, list):
        return False, ["Missing or non-array 'questions' field"], [], []
    if not raw_questions:
        return False, ["No questions were extracted from the document"], [], []

    questions: list[dict[str, Any]] = []
    seen_numbers: set[int] = set()

    for idx, row in enumerate(raw_questions):
        label = f"Question row {idx + 1}"
        if not isinstance(row, dict):
            warnings.append(f"{label} dropped: entry is not an object")
            continue

        # --- number -------------------------------------------------------
        try:
            number = int(row.get("number"))
        except (TypeError, ValueError):
            warnings.append(f"{label} dropped: 'number' is not an integer ({row.get('number')!r})")
            continue
        if number <= 0:
            warnings.append(f"{label} dropped: 'number' must be positive ({number})")
            continue
        if number in seen_numbers:
            warnings.append(f"Question {number} appears more than once — kept the first occurrence")
            continue
        seen_numbers.add(number)

        # --- text ----------------------------------------------------------
        text = str(row.get("text") or "").strip()
        if not text:
            warnings.append(f"{label} (number {number}) dropped: question text is empty")
            continue
        if len(text) > MAX_QUESTION_TEXT_CHARS:
            warnings.append(f"Question {number}: text truncated to {MAX_QUESTION_TEXT_CHARS} characters")
            text = text[:MAX_QUESTION_TEXT_CHARS]

        # --- max_score ------------------------------------------------------
        try:
            max_score = float(row.get("max_score"))
        except (TypeError, ValueError):
            warnings.append(f"{label} (number {number}) dropped: 'max_score' is not numeric ({row.get('max_score')!r})")
            continue
        if max_score <= 0:
            warnings.append(f"{label} (number {number}) dropped: 'max_score' must be positive ({max_score})")
            continue

        # --- criteria -------------------------------------------------------
        raw_criteria = row.get("criteria")
        if not isinstance(raw_criteria, list) or not raw_criteria:
            warnings.append(f"{label} (number {number}) dropped: criteria array is empty or missing")
            continue

        criteria: list[dict[str, Any]] = []
        for c_idx, c in enumerate(raw_criteria):
            if not isinstance(c, dict):
                warnings.append(f"Question {number} criterion row {c_idx + 1} dropped: entry is not an object")
                continue
            cid = str(c.get("id") or "").strip()
            if not cid:
                warnings.append(f"Question {number} criterion row {c_idx + 1} dropped: empty id")
                continue
            desc = str(c.get("description") or "").strip()
            if not desc:
                warnings.append(f"Question {number} criterion '{cid}' dropped: empty description")
                continue
            if len(desc) > MAX_CRITERION_DESC_CHARS:
                warnings.append(f"Question {number} criterion '{cid}': description truncated to {MAX_CRITERION_DESC_CHARS} characters")
                desc = desc[:MAX_CRITERION_DESC_CHARS]
            try:
                c_max = float(c.get("max_score"))
            except (TypeError, ValueError):
                warnings.append(f"Question {number} criterion '{cid}' dropped: 'max_score' is not numeric ({c.get('max_score')!r})")
                continue
            if c_max <= 0:
                warnings.append(f"Question {number} criterion '{cid}' dropped: 'max_score' must be positive ({c_max})")
                continue
            criteria.append({"id": cid, "description": desc, "max_score": c_max})

        if not criteria:
            warnings.append(f"{label} (number {number}) dropped: no valid criteria remained")
            continue

        # Keep the row but warn — the preview is editable, the teacher raises
        # maxScore or trims criteria before applying.
        crit_sum = sum(c["max_score"] for c in criteria)
        if crit_sum > max_score + 1e-9:
            warnings.append(
                f"Question {number}: criteria sum ({crit_sum:g}) exceeds max_score ({max_score:g}) — fix in the preview before applying"
            )

        questions.append({
            "number": number,
            "text": text,
            "max_score": max_score,
            "criteria": criteria,
        })

    if not questions:
        errors.append("All extracted question rows were invalid")
        return False, errors, [], warnings

    return True, errors, questions, warnings
