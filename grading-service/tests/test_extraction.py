"""Tests for exam-rubric extraction validation and the extract prompt contract."""
import pytest

from app.extraction_validation import validate_extraction
from app.prompts.loader import load_prompt


class TestValidateExtraction:
    def test_happy_path(self):
        parsed = {
            "questions": [
                {
                    "number": 1,
                    "text": "بسنجید",
                    "max_score": 20,
                    "criteria": [
                        {"id": "الف", "description": "رسم نمودار", "max_score": 10},
                        {"id": "ب", "description": "محاسبه کشش", "max_score": 10},
                    ],
                }
            ]
        }
        is_valid, errors, questions, warnings = validate_extraction(parsed, "raw")
        assert is_valid
        assert errors == []
        assert warnings == []
        assert len(questions) == 1
        assert questions[0]["number"] == 1
        assert questions[0]["max_score"] == 20
        assert [c["id"] for c in questions[0]["criteria"]] == ["الف", "ب"]

    def test_unparseable_json_invalid(self):
        is_valid, errors, questions, warnings = validate_extraction(None, "raw")
        assert not is_valid
        assert errors == ["Response is not valid JSON"]
        assert questions == []

    def test_non_dict_invalid(self):
        is_valid, errors, _, _ = validate_extraction(["x"], "raw")
        assert not is_valid
        assert "Parsed JSON is not an object" in errors

    def test_missing_questions_field_invalid(self):
        is_valid, errors, _, _ = validate_extraction({"other": 1}, "raw")
        assert not is_valid
        assert any("questions" in e for e in errors)

    def test_empty_questions_invalid(self):
        is_valid, errors, _, _ = validate_extraction({"questions": []}, "raw")
        assert not is_valid
        assert any("No questions were extracted" in e for e in errors)

    def test_bad_number_drops_row(self):
        parsed = {"questions": [
            {"number": "x", "text": "t", "max_score": 5, "criteria": [{"id": "a", "description": "d", "max_score": 5}]},
            {"number": 0, "text": "t", "max_score": 5, "criteria": [{"id": "a", "description": "d", "max_score": 5}]},
            {"number": 3, "text": "ok", "max_score": 5, "criteria": [{"id": "a", "description": "d", "max_score": 5}]},
        ]}
        is_valid, errors, questions, warnings = validate_extraction(parsed, "raw")
        assert is_valid
        assert len(questions) == 1
        assert questions[0]["number"] == 3
        assert len(warnings) == 2

    def test_all_rows_bad_number_invalidates_batch(self):
        parsed = {"questions": [
            {"number": "x", "text": "t", "max_score": 5, "criteria": [{"id": "a", "description": "d", "max_score": 5}]},
        ]}
        is_valid, errors, questions, warnings = validate_extraction(parsed, "raw")
        assert not is_valid
        assert questions == []
        assert any("All extracted question rows were invalid" in e for e in errors)

    def test_empty_text_drops_row(self):
        parsed = {"questions": [
            {"number": 1, "text": "   ", "max_score": 5, "criteria": [{"id": "a", "description": "d", "max_score": 5}]},
            {"number": 2, "text": "ok", "max_score": 5, "criteria": [{"id": "a", "description": "d", "max_score": 5}]},
        ]}
        is_valid, _, questions, warnings = validate_extraction(parsed, "raw")
        assert is_valid
        assert [q["number"] for q in questions] == [2]
        assert any("empty" in w for w in warnings)

    def test_zero_max_score_drops_row(self):
        parsed = {"questions": [
            {"number": 1, "text": "t", "max_score": 0, "criteria": [{"id": "a", "description": "d", "max_score": 1}]},
        ]}
        _, _, questions, warnings = validate_extraction(parsed, "raw")
        assert questions == []
        assert any("max_score" in w for w in warnings)

    def test_empty_criteria_drops_row(self):
        parsed = {"questions": [
            {"number": 1, "text": "t", "max_score": 5, "criteria": []},
        ]}
        _, _, questions, warnings = validate_extraction(parsed, "raw")
        assert questions == []
        assert any("criteria" in w for w in warnings)

    def test_bad_criterion_dropped_but_row_kept(self):
        parsed = {"questions": [
            {"number": 1, "text": "t", "max_score": 5, "criteria": [
                {"id": "a", "description": "d", "max_score": 3},
                {"id": "", "description": "no id", "max_score": 2},
                {"id": "c", "description": "d", "max_score": -1},
            ]},
        ]}
        _, _, questions, _ = validate_extraction(parsed, "raw")
        assert len(questions) == 1
        assert len(questions[0]["criteria"]) == 1
        assert questions[0]["criteria"][0]["id"] == "a"

    def test_all_rows_invalid_marks_whole_batch(self):
        parsed = {"questions": [
            {"number": 1, "text": "", "max_score": 5, "criteria": [{"id": "a", "description": "d", "max_score": 1}]},
        ]}
        is_valid, errors, questions, _ = validate_extraction(parsed, "raw")
        assert not is_valid
        assert questions == []
        assert any("All extracted question rows were invalid" in e for e in errors)

    def test_duplicate_numbers_kept_first(self):
        parsed = {"questions": [
            {"number": 1, "text": "first", "max_score": 5, "criteria": [{"id": "a", "description": "d", "max_score": 5}]},
            {"number": 1, "text": "second", "max_score": 5, "criteria": [{"id": "a", "description": "d", "max_score": 5}]},
            {"number": 2, "text": "ok", "max_score": 5, "criteria": [{"id": "a", "description": "d", "max_score": 5}]},
        ]}
        _, _, questions, warnings = validate_extraction(parsed, "raw")
        assert len(questions) == 2
        assert questions[0]["text"] == "first"
        assert questions[1]["number"] == 2
        assert any("more than once" in w for w in warnings)

    def test_criteria_sum_exceeds_kept_with_warning(self):
        # Over-sum rows are KEPT — the preview dialog is editable and the
        # teacher fixes them before applying; only apply re-validates hard.
        parsed = {"questions": [
            {"number": 1, "text": "t", "max_score": 5, "criteria": [
                {"id": "a", "description": "d1", "max_score": 4},
                {"id": "b", "description": "d2", "max_score": 4},
            ]},
        ]}
        is_valid, errors, questions, warnings = validate_extraction(parsed, "raw")
        assert is_valid
        assert errors == []
        assert len(questions) == 1
        assert any("exceeds max_score" in w for w in warnings)

    def test_string_numbers_coerced(self):
        parsed = {"questions": [
            {"number": "2", "text": "t", "max_score": "10.5", "criteria": [
                {"id": "a", "description": "d", "max_score": "10.5"},
            ]},
        ]}
        is_valid, _, questions, _ = validate_extraction(parsed, "raw")
        assert is_valid
        assert questions[0]["number"] == 2
        assert questions[0]["max_score"] == 10.5

    def test_overlong_text_truncated(self):
        parsed = {"questions": [
            {"number": 1, "text": "x" * 9000, "max_score": 5, "criteria": [{"id": "a", "description": "d", "max_score": 5}]},
        ]}
        _, _, questions, warnings = validate_extraction(parsed, "raw")
        assert len(questions[0]["text"]) == 8000
        assert any("truncated" in w for w in warnings)


class TestExtractPromptContract:
    def test_extract_prompt_loads_and_renders(self):
        text = load_prompt("extract")
        assert "{document_name}" in text
        assert "{document_text}" in text
        assert "{document_pages}" in text
        # Escaped braces survive formatting
        rendered = text.format(document_name="k.pdf", document_pages=2, document_text="T")
        assert "{{" not in rendered
        assert '"questions"' in rendered

    def test_extract_prompt_verbatim_language_rule(self):
        text = load_prompt("extract")
        assert "VERBATIM" in text
        assert "translate" in text.lower()
