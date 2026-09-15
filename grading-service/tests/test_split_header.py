"""M6 split-header helpers — digit normalization + student-id extraction."""
from __future__ import annotations

import pytest

from app.split_header import extract_student_id, normalize_digits


class TestNormalizeDigits:
    def test_persian_digits_become_ascii(self):
        assert normalize_digits("۰۱۲۳۴۵۶۷۸۹") == "0123456789"

    def test_arabic_indic_digits_become_ascii(self):
        assert normalize_digits("٠١٢٣٤٥٦٧٨٩") == "0123456789"

    def test_ascii_digits_untouched(self):
        assert normalize_digits("4021131") == "4021131"

    def test_mixed_digits(self):
        assert normalize_digits("402۱۱۳1") == "4021131"

    def test_separators_squeezed(self):
        # dashes/dots/slashes/spaces become single spaces — digit GROUPS stay separated
        assert normalize_digits("40-21-13") == "40 21 13"
        assert normalize_digits("40.21.13") == "40 21 13"
        assert normalize_digits("40 21/13") == "40 21 13"

    def test_persian_thousands_separator(self):
        # ٬ (U+066C thousands sep) and ZWNJ are separators → single space, so
        # the number stays as two digit GROUPS the regex can rejoin later
        assert normalize_digits("۴۰۲٬۱۱۳") == "402 113"


class TestExtractStudentId:
    def test_labeled_id_wins(self):
        rid, labeled = extract_student_id("نام: علی شماره دانشجویی: ۴۰۲۱۱۳۱")
        assert rid == "4021131"
        assert labeled is True

    def test_labeled_english(self):
        rid, labeled = extract_student_id("Name: Ali Student No.: 4021131")
        assert rid == "4021131"
        assert labeled is True

    def test_longest_unlabeled_run(self):
        rid, labeled = extract_student_id("درس اقتصاد خرد ۲ تاریخ 1402/06/21")
        # runs: "1402" — longest single run wins, never joins groups
        assert rid == "1402"
        assert labeled is False

    def test_unlabeled_never_joins_groups(self):
        # a date "1402 06 21" must stay three numbers; unlabeled path returns
        # the longest single run only
        rid, labeled = extract_student_id("تاریخ 1402 06 21")
        assert rid == "1402"
        assert labeled is False

    def test_multi_group_id_joined_when_labelled(self):
        # dashes between groups: after the label, joining is safe
        rid, labeled = extract_student_id("شماره دانشجویی: 40-21-13")
        assert rid == "402113"
        assert labeled is True

    def test_no_id_found(self):
        rid, labeled = extract_student_id("نام: علی")
        assert rid is None
        assert labeled is False

    def test_short_runs_ignored(self):
        # runs under 3 digits (e.g. a page number "1") are noise
        rid, labeled = extract_student_id("صفحه 1")
        assert rid is None
        assert labeled is False

    def test_oversized_run_rejected(self):
        # 16+ digits = a serial, not a student number — rejected entirely
        rid, labeled = extract_student_id("ID: 1234567890123456")
        assert rid is None
        assert labeled is False

    def test_empty(self):
        assert extract_student_id("") == (None, False)
