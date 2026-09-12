"""Tests for the grading service — validation, evaluation metrics, prompt loading."""
import pytest
from app.validation import validate_grading_response, parse_json_safe
from app.evaluation import compute_metrics
from app.prompts.loader import load_prompt, list_prompt_versions


RUBRIC = {
    "criteria": [
        {"id": "1a", "description": "Identify the concept", "max_score": 2},
        {"id": "1b", "description": "Apply to scenario", "max_score": 3},
    ]
}


class TestValidation:
    def test_valid_response(self):
        parsed = {
            "score": 4,
            "reasoning": "Good answer",
            "criteria_scores": [
                {"id": "1a", "score": 2, "max_score": 2},
                {"id": "1b", "score": 2, "max_score": 3},
            ],
            "confidence": 0.9,
        }
        is_valid, errors = validate_grading_response(parsed, "", 5.0, RUBRIC)
        assert is_valid
        assert errors == []

    def test_score_out_of_range(self):
        parsed = {"score": 6.0, "reasoning": "x", "criteria_scores": []}
        is_valid, errors = validate_grading_response(parsed, "", 5.0, RUBRIC)
        assert not is_valid
        assert any("outside" in e for e in errors)

    def test_criterion_over_max(self):
        parsed = {
            "score": 5,
            "reasoning": "x",
            "criteria_scores": [{"id": "1a", "score": 3, "max_score": 2}],
        }
        is_valid, errors = validate_grading_response(parsed, "", 5.0, RUBRIC)
        assert not is_valid
        assert any("outside" in e for e in errors)

    def test_criteria_sum_exceeds_max(self):
        parsed = {
            "score": 6,
            "reasoning": "x",
            "criteria_scores": [
                {"id": "1a", "score": 2, "max_score": 2},
                {"id": "1b", "score": 4, "max_score": 3},  # sum = 6 > 5
            ],
        }
        is_valid, errors = validate_grading_response(parsed, "", 5.0, RUBRIC)
        assert not is_valid
        assert any("exceeds total max_score" in e for e in errors)

    def test_missing_field(self):
        is_valid, errors = validate_grading_response({"reasoning": "hi"}, "", 5.0, RUBRIC)
        assert not is_valid
        assert any("Missing required field 'score'" in e for e in errors)

    def test_missing_rubric_criterion_in_response(self):
        parsed = {"score": 2, "reasoning": "x", "criteria_scores": [{"id": "1a", "score": 2, "max_score": 2}]}
        is_valid, errors = validate_grading_response(parsed, "", 5.0, RUBRIC)
        assert not is_valid
        assert any("missing from response" in e for e in errors)

    def test_invalid_json(self):
        parsed, err = parse_json_safe("this is not json at all")
        assert parsed is None
        assert err is not None

    def test_json_with_markdown_fences(self):
        parsed, err = parse_json_safe('```json\n{"score": 4}\n```')
        assert err is None
        assert parsed == {"score": 4}

    def test_confidence_out_of_range(self):
        parsed = {"score": 3, "reasoning": "x", "criteria_scores": [], "confidence": 1.5}
        is_valid, _ = validate_grading_response(parsed, "", 5.0, RUBRIC)
        assert not is_valid

    def test_unlisted_criteria_always_rejected(self):
        # Rubrics always arrive as structured rows from the DB — criterion ids
        # not defined there are rejected unconditionally.
        empty_rubric = {"criteria": []}
        parsed = {
            "score": 4,
            "reasoning": "x",
            "criteria_scores": [{"id": "anything", "score": 2, "max_score": 2}],
        }
        is_valid, errors = validate_grading_response(parsed, "", 5.0, empty_rubric)
        assert not is_valid
        assert any("not defined in the rubric" in e for e in errors)


class TestEvaluation:
    def test_perfect_agreement(self):
        m = compute_metrics([5, 4, 3, 2], [5, 4, 3, 2])
        assert m.mae == 0
        assert m.rmse == 0
        assert m.exact_agreement_pct == 100
        assert m.bias == 0
        assert m.quadratic_weighted_kappa == 1.0

    def test_known_bias(self):
        m = compute_metrics([5, 4, 3], [6, 5, 4])
        assert m.bias == 1.0
        assert m.exact_agreement_pct == 0
        assert m.within_one_pct == 100

    def test_pearson(self):
        m = compute_metrics([1, 2, 3, 4, 5], [2, 3, 4, 5, 6])
        assert m.pearson_r is not None
        assert abs(m.pearson_r - 1.0) < 1e-6

    def test_qwk_range(self):
        m = compute_metrics([1, 2, 3, 4, 5], [5, 4, 3, 2, 1])
        assert -1 <= m.quadratic_weighted_kappa <= 1


class TestPrompts:
    def test_load_default(self):
        text = load_prompt("default")
        assert "{question_text}" in text
        assert "{rubric_json}" in text
        assert "{max_score}" in text

    def test_list_versions(self):
        versions = list_prompt_versions()
        assert "default" in versions