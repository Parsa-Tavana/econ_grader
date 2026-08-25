"""Tests for attachment preparation — question/rubric/answer role routing."""
import zipfile

import pytest

from app.attachments import prepare_attachments


def _make_docx(path, paragraphs):
    """Minimal valid .docx (word/document.xml with paragraph runs)."""
    body = "".join(f"<w:p><w:r><w:t>{p}</w:t></w:r></w:p>" for p in paragraphs)
    document = (
        '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
        '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
        f"<w:body>{body}</w:body></w:document>"
    )
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("word/document.xml", document)


class TestCombinedQuestionText:
    def test_typed_text_only(self):
        prep = prepare_attachments([], [])
        assert prep.combined_question_text("Explain elasticity.") == "Explain elasticity."

    def test_document_text_only(self, tmp_path):
        docx = tmp_path / "q.docx"
        _make_docx(docx, ["Printed question line."])
        prep = prepare_attachments([], [str(docx)])
        assert "Printed question line." in prep.question_extra_text
        combined = prep.combined_question_text("")
        assert combined == "Printed question line."

    def test_typed_and_document_merge(self, tmp_path):
        docx = tmp_path / "q.docx"
        _make_docx(docx, ["Document part of the question."])
        prep = prepare_attachments([], [str(docx)])
        combined = prep.combined_question_text("Typed part of the question.")
        assert "Typed part of the question." in combined
        assert "Document part of the question." in combined
        # Both halves survive as one statement, separated by a blank line
        assert combined.index("Typed") < combined.index("Document")

    def test_empty_everywhere(self):
        prep = prepare_attachments([], [])
        assert prep.combined_question_text("") == ""
        assert prep.combined_question_text("  ") == ""


class TestRoleRouting:
    def test_rubric_and_question_docs_kept_apart(self, tmp_path):
        qdoc = tmp_path / "question.docx"
        rdoc = tmp_path / "rubric.docx"
        _make_docx(qdoc, ["Question statement."])
        _make_docx(rdoc, ["Rubric criteria text."])
        prep = prepare_attachments([], [str(qdoc)], [str(rdoc)])

        assert "Question statement." in prep.question_extra_text
        assert "Rubric criteria text." in prep.rubric_extra_text
        # No cross-contamination between the two roles
        assert "Rubric" not in prep.question_extra_text.replace("[", "").replace("]", "")
        assert "Question statement." not in prep.rubric_extra_text

    def test_answer_doc_lands_in_extra_text(self, tmp_path):
        adoc = tmp_path / "answer.docx"
        _make_docx(adoc, ["Student typed answer."])
        prep = prepare_attachments([str(adoc)], [], [])
        assert "Student typed answer." in prep.extra_text
        assert prep.question_extra_text == ""
        assert prep.rubric_extra_text == ""

    def test_rubric_docx_gets_header(self, tmp_path):
        rdoc = tmp_path / "rubric.docx"
        _make_docx(rdoc, ["Criterion A: 2 points."])
        prep = prepare_attachments([], [], [str(rdoc)])
        assert "[Rubric document: rubric.docx]" in prep.rubric_extra_text

    def test_question_doc_has_no_typed_document_header(self, tmp_path):
        qdoc = tmp_path / "paper.docx"
        _make_docx(qdoc, ["The printed question."])
        prep = prepare_attachments([], [str(qdoc)], [])
        assert "[Typed document" not in prep.question_extra_text
        assert "The printed question." in prep.question_extra_text

    def test_no_rubric_paths_backwards_compatible(self):
        """Two-arg call (old signature) still works."""
        prep = prepare_attachments([], [])
        assert prep.rubric_images == []
        assert prep.rubric_extra_text == ""
