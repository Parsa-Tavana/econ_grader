"""Validation rules for AI grading responses.

Covers the strictness contract:
  - the headline `score` MUST equal the sum of criteria_scores (a generous
    score that the model's own criteria do not add up to is rejected)
  - a criterion may not declare a max_score exceeding the rubric's
  - range checks: score in [0, max], each criterion in [0, its max]
  - every rubric criterion must appear in the response

Also asserts the default prompt template renders with the strictness rules.
"""
import json

import pytest

from app.validation import validate_grading_response
from app.prompts.loader import load_prompt

RUBRIC = {"criteria": [
    {"id": "1a", "description": "d1", "max_score": 2},
    {"id": "1b", "description": "d2", "max_score": 3},
]}


def _resp(score, crits):
    return {
        "score": score,
        "reasoning": "r",
        "criteria_scores": crits,
        "confidence": 0.9,
        "flagged_ambiguities": [],
    }


def _crit(cid, score, max_score):
    return {"id": cid, "score": score, "max_score": max_score, "comment": "c"}


class TestScoreSumConsistency:
    def test_consistent_response_is_valid(self):
        ok, errs = validate_grading_response(
            _resp(4, [_crit("1a", 2, 2), _crit("1b", 2, 3)]), "raw", 5, RUBRIC)
        assert ok, errs

    def test_headline_score_must_equal_criteria_sum(self):
        # A generous headline score that the model's own criteria do not add
        # up to must be rejected, not stored as a valid run.
        ok, errs = validate_grading_response(
            _resp(5, [_crit("1a", 2, 2), _crit("1b", 1, 3)]), "raw", 5, RUBRIC)
        assert not ok
        assert any("does not match the sum" in e for e in errs)

    def test_small_rounding_tolerance_is_allowed(self):
        ok, errs = validate_grading_response(
            _resp(4.02, [_crit("1a", 2, 2), _crit("1b", 2, 3)]), "raw", 5, RUBRIC)
        assert ok, errs


class TestRangeChecks:
    def test_criterion_score_above_its_max_is_rejected(self):
        ok, errs = validate_grading_response(
            _resp(5.5, [_crit("1a", 2, 2), _crit("1b", 3.5, 3)]), "raw", 5, RUBRIC)
        assert not ok
        assert any("outside [0, 3" in e for e in errs)

    def test_score_outside_total_range_is_rejected(self):
        ok, errs = validate_grading_response(
            _resp(6, [_crit("1a", 2, 2), _crit("1b", 3, 3)]), "raw", 5, RUBRIC)
        assert not ok
        assert any("outside allowed range" in e for e in errs)

    def test_criteria_sum_above_total_is_rejected(self):
        ok, errs = validate_grading_response(
            _resp(5, [_crit("1a", 2, 2), _crit("1b", 3.5, 3)]), "raw", 5, RUBRIC)
        assert not ok
        assert any("exceeds total max_score" in e for e in errs)


class TestInflatedMaxDetection:
    def test_declared_max_exceeding_rubric_max_is_rejected(self):
        # A hallucinated larger max quietly legitimizes inflated scores.
        ok, errs = validate_grading_response(
            _resp(4, [_crit("1a", 2, 4), _crit("1b", 2, 3)]), "raw", 5, RUBRIC)
        assert not ok
        assert any("exceeding the rubric" in e for e in errs)


class TestRubricCoverage:
    def test_missing_criterion_is_rejected(self):
        ok, errs = validate_grading_response(
            _resp(2, [_crit("1a", 2, 2)]), "raw", 5, RUBRIC)
        assert not ok
        assert any("missing" in e.lower() and "1b" in e for e in errs)

    def test_unlisted_criterion_is_rejected_without_document_flag(self):
        ok, errs = validate_grading_response(
            _resp(2, [_crit("1a", 2, 2), _crit("invented", 0, 3)]), "raw", 5, RUBRIC)
        assert not ok
        assert any("not defined in the rubric" in e for e in errs)


class TestDefaultPromptContract:
    """The default prompt must carry the anti-forgiveness rules and render."""

    def test_strictness_rules_present_and_template_renders(self):
        rendered = load_prompt("default").format(
            question_text="Explain demand.",
            rubric_json=json.dumps(RUBRIC),
            max_score=5,
        )
        assert "ZERO BY DEFAULT" in rendered
        assert "VISUAL CRITERIA" in rendered          # describing is not drawing
        assert "MUST be exactly the sum" in rendered   # sum consistency rule
        assert "1a" in rendered                        # rubric injected

    def test_placeholders_are_exactly_the_supported_set(self):
        template = load_prompt("default")
        rendered = template.format(question_text="q", rubric_json="[]", max_score=5)
        # No unreplaced placeholders may survive formatting.
        assert "{" + "question_text" + "}" not in rendered
        assert "{" + "rubric_json" + "}" not in rendered
        assert "{" + "max_score" + "}" not in rendered
