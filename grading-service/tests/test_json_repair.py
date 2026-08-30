"""Tests for hardened JSON parsing of model output.

Regression tests for the 2026-08-26 live incident: a transient unescaped-quote
glitch in Claude's output turned a good grading run into isValid=false,
aiScore=0.0 ("Claude API failure: Expecting property name enclosed in double
quotes"). The parser must repair common malformations instead.
"""
import os

import pytest

from app.json_repair_util import parse_json_hardened
from app.validation import parse_json_safe
from app.graders.base import ERROR_KIND_PARSE, ModelOutputParseError


# The exact shape observed live 2026-08-26: unescaped inner quotes inside a
# string value at line 3 of the response JSON.
LIVE_INCIDENT_RAW = (
    '{\n'
    '  "score": 4,\n'
    '  "reasoning": "the student says "demand" falls when price rises",\n'
    '  "criteria_scores": [\n'
    '    {"id": "1a", "score": 2, "max_score": 2},\n'
    '    {"id": "1b", "score": 2, "max_score": 3}\n'
    '  ],\n'
    '  "confidence": 0.8\n'
    '}'
)


class TestParseJsonHardened:
    def test_clean_json(self):
        parsed, err = parse_json_hardened('{"score": 4, "reasoning": "good"}')
        assert err is None
        assert parsed == {"score": 4, "reasoning": "good"}

    def test_unescaped_inner_quotes_live_incident(self):
        parsed, err = parse_json_hardened(LIVE_INCIDENT_RAW)
        assert err is None, f"live incident output must parse, got: {err}"
        assert parsed["score"] == 4
        # Inner quotes preserved as content, not structural
        assert parsed["reasoning"] == 'the student says "demand" falls when price rises'
        assert len(parsed["criteria_scores"]) == 2

    def test_trailing_comma_in_array(self):
        raw = '{"score": 4, "criteria_scores": [{"id": "1a", "score": 2,}], "reasoning": "x"}'
        parsed, err = parse_json_hardened(raw)
        assert err is None
        assert parsed["criteria_scores"] == [{"id": "1a", "score": 2}]

    def test_trailing_comma_after_last_property(self):
        raw = '{"score": 4, "reasoning": "ok", "criteria_scores": [],}'
        parsed, err = parse_json_hardened(raw)
        assert err is None
        assert parsed["score"] == 4

    def test_trailing_comma_inside_string_not_touched(self):
        # A comma inside a quoted value must survive intact
        raw = '{"score": 4, "reasoning": "a, b , c", "criteria_scores": []}'
        parsed, err = parse_json_hardened(raw)
        assert err is None
        assert parsed["reasoning"] == "a, b , c"

    def test_quotes_and_trailing_comma_combined(self):
        raw = '{"score": 3, "reasoning": "said "shift" out loud", "criteria_scores": [],}'
        parsed, err = parse_json_hardened(raw)
        assert err is None
        assert parsed["reasoning"] == 'said "shift" out loud'

    def test_markdown_fences(self):
        raw = '```json\n{"score": 4, "reasoning": "fenced"}\n```'
        parsed, err = parse_json_hardened(raw)
        assert err is None
        assert parsed == {"score": 4, "reasoning": "fenced"}

    def test_unbalanced_opening_fence(self):
        raw = '```json\n{"score": 4, "reasoning": "no close fence"}'
        parsed, err = parse_json_hardened(raw)
        assert err is None
        assert parsed["score"] == 4

    def test_prose_around_json(self):
        raw = 'Sure! Here is the grading:\n{"score": 5, "reasoning": "great"}\nHope that helps.'
        parsed, err = parse_json_hardened(raw)
        assert err is None
        assert parsed["score"] == 5

    def test_garbage_still_fails_cleanly(self):
        parsed, err = parse_json_hardened("this is not json at all")
        assert parsed is None
        assert err is not None
        assert "JSON decode failed" in err

    def test_empty_input_fails_cleanly(self):
        for empty in ["", "   \n\t  "]:
            parsed, err = parse_json_hardened(empty)
            assert parsed is None
            assert err == "Empty response text"

    def test_never_raises_on_hostile_input(self):
        hostile = [
            '{"score"',
            '{{{{{',
            '}}}}}',
            '"just a string"',
            '[1, 2, 3]',
            '{"score": 04, "reasoning": "x"}',  # leading-zero number
        ]
        for raw in hostile:
            parsed, err = parse_json_hardened(raw)  # must not raise
            assert (parsed is None) == (err is not None)

    def test_non_dict_json_is_an_error(self):
        parsed, err = parse_json_hardened('[1, 2, 3]')
        assert parsed is None
        assert err is not None


class TestParseJsonSafeDelegation:
    """main.py validates via parse_json_safe — it must get the same repairs."""

    def test_delegates_to_hardened_parser(self):
        parsed, err = parse_json_safe(LIVE_INCIDENT_RAW)
        assert err is None
        assert parsed["score"] == 4

    def test_garbage_returns_error(self):
        parsed, err = parse_json_safe("nope")
        assert parsed is None
        assert err is not None


class TestGraderParseErrorTagging:
    """Graders tag hopeless output with ERROR_KIND_PARSE so /grade can retry."""

    def _make_grader_result_via_parse(self, grader_cls, raw):
        # _parse_response raises ModelOutputParseError on unparseable text;
        # grade() catches it and tags the result. We exercise the static method
        # + exception type directly (grade() needs API keys/images).
        with pytest.raises(ModelOutputParseError):
            grader_cls._parse_response(raw)

    def test_glm_parse_error_type(self):
        pytest.importorskip("httpx")
        from app.graders.glm_grader import GlmVisionGrader
        self._make_grader_result_via_parse(GlmVisionGrader, "utterly not json")

    def test_gpt_parse_error_type(self):
        pytest.importorskip("httpx")
        from app.graders.gpt_grader import GptVisionGrader
        self._make_grader_result_via_parse(GptVisionGrader, "utterly not json")

    def test_all_graders_share_the_hardened_parser(self):
        import ast
        import inspect
        try:
            from app.graders.glm_grader import GlmVisionGrader
            from app.graders.gpt_grader import GptVisionGrader
            classes = [GlmVisionGrader, GptVisionGrader]
            for cls in classes:
                src = inspect.getsource(cls._parse_response)
                assert "parse_json_hardened" in src, cls.__name__
        except ImportError:
            # Minimal env without httpx: verify via source scan instead.
            here = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
            for name in ("glm_grader", "gpt_grader", "openai_compatible"):
                path = os.path.join(here, "app", "graders", f"{name}.py")
                tree = ast.parse(open(path, encoding="utf-8").read())
                found = any(
                    isinstance(node, ast.Name) and node.id == "parse_json_hardened"
                    for node in ast.walk(tree)
                )
                assert found, f"{name}.py does not use parse_json_hardened"

    def test_error_kind_constant(self):
        assert ERROR_KIND_PARSE == "parse"
