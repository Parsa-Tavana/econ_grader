"""M6 bulk answer-sheet split — header-band OCR endpoint.

`POST /split-header` consumes absolute page-image paths (shared volume, same
contract as /ingest) and, for each page:

  1. crops the fixed header band (top N% of the page — where students write
     their student number on the physical answer sheet),
  2. OCRs that band with tesseract (Persian `fas` + English `eng` digits),
  3. normalizes Persian digits ۰۱۲۳۴۵۶۷۸۹ (and Arabic ٠١٢٣٤٥٦٧٨٩) to ASCII,
     strips separators/dashes, and extracts a student identifier,
  4. assigns a per-page confidence (0..1) from tesseract's word confidences.

No AI tokens are consumed — this is pure OCR. The .NET side owns grouping
(consecutive same-ID pages → per-student stack) and the accept threshold
(never auto-assign below Split:OcrConfidenceThreshold); Python only reports
per-page facts (raw id string + confidence) so the review UI can show all.
"""
from __future__ import annotations

import logging
import re
from pathlib import Path

from fastapi import Depends, HTTPException

from .config import settings
from .schemas import SplitHeaderRequest, SplitHeaderResponse, SplitHeaderPageOut
from .internal_auth import require_internal_key

logger = logging.getLogger("grading-service.split_header")

# Persian ۰-۹ (U+06F0..U+06F9) and Arabic-Indic ٠-٩ (U+0660..U+0669) → ASCII.
_DIGIT_TABLE = str.maketrans(
    "۰۱۲۳۴۵۶۷۸۹" "٠١٢٣٤٥٦٧٨٩",
    "0123456789" "0123456789",
)

# Characters that separate digit groups on paper (spaces, dashes, dots,
# slashes, Persian thousands separator ٬ and ZWNJ). Squeezed to single spaces
# so digit GROUPS stay separated — joining them is a deliberate decision
# made only in the labeled path below.
_SEPARATOR_RE = re.compile(r"[\s\-–—._/\\٬‌]")

# A plausible student id: 3-15 digits with a non-digit boundary on BOTH sides
# (a 16-digit serial is a barcode, not what a student writes — reject it
# entirely rather than truncating).
_ID_RE = re.compile(r"(?<!\d)(\d{3,15})(?!\d)")
# Looser variant for the LABELED path only: paper forms often break the
# number into 2-digit groups with dashes ("40-21-13"); after an explicit
# student-number label those groups are joined before the 3-15 check.
_DIGIT_RUN_RE = re.compile(r"(?<!\d)(\d{2,15})(?!\d)")

# Labels the OCR text may carry before the number ("شماره دانشجویی:",
# "Student No.", "کد ملی" …). Stripped so they never pollute the id.
_LABEL_RE = re.compile(
    r"(?:student\s*(?:no|number|id)|id\s*no|شماره\s*دانشجویی|کد\s*دانشجویی|"
    r"شماره\s*دانش\s*آموزی|کد\s*ملی|شماره\s*ملی|دانشجویی)\s*[:：\-]?",
    re.IGNORECASE,
)


def normalize_digits(text: str) -> str:
    """Persian/Arabic digits → ASCII, then squeeze separator runs to single
    spaces so the id regex sees clean digit groups."""
    return _SEPARATOR_RE.sub(" ", text.translate(_DIGIT_TABLE))


def extract_student_id(header_text: str) -> tuple[str | None, bool]:
    """Pull the most plausible student id out of normalized header text.

    Returns (id or None, used_label). Heuristics, in order:
      - a digit run right after an explicit student-number label wins; when
        the post-label chunk holds several short runs (dashes between groups,
        e.g. "40-21-13") their concatenation is accepted — the label says
        this IS the student number, so joining groups is safe;
      - otherwise the LONGEST single digit run anywhere in the text (a
        student number is usually the only long number on the header band);
        unlabeled matches never join groups (a date "1402 06 21" must stay
        three numbers).
    """
    normalized = normalize_digits(header_text)

    # Labeled first: everything AFTER the first student-number label.
    for chunk in _LABEL_RE.split(normalized)[1:]:
        runs = _DIGIT_RUN_RE.findall(chunk)
        if not runs:
            continue
        single = next((r for r in runs if 3 <= len(r) <= 15), None)
        if single is not None:
            return single, True
        joined = "".join(runs)
        if 3 <= len(joined) <= 15:
            return joined, True

    # Unlabeled fallback: longest single run anywhere.
    best = None
    for run in _ID_RE.findall(normalized):
        if best is None or len(run) > len(best):
            best = run
    return (best, False) if best is not None else (None, False)


def ocr_header_band(image_path: str, header_band_pct: float) -> tuple[str, float]:
    """Crop the top header band and OCR it with tesseract (fas+eng).

    Returns (text, mean_word_confidence 0..1). Raises RuntimeError when the
    tesseract binary or a language pack is missing so the .NET side records a
    FAILED job instead of silently marking every page unmatched.
    """
    try:
        import pytesseract
        from PIL import Image
    except ImportError as exc:  # pragma: no cover - container ships both
        raise RuntimeError(f"OCR dependencies missing: {exc}") from exc

    from PIL import Image

    with Image.open(image_path) as im:
        im = im.convert("RGB")
        band_height = max(1, int(im.height * header_band_pct / 100.0))
        band = im.crop((0, 0, im.width, band_height))

        try:
            data = pytesseract.image_to_data(
                band, lang="fas+eng", output_type=pytesseract.Output.DICT
            )
        except pytesseract.TesseractNotFoundError as exc:
            raise RuntimeError(
                f"tesseract binary or 'fas'/'eng' language pack missing: {exc}"
            ) from exc

    words: list[str] = []
    confs: list[float] = []
    for text, conf in zip(data.get("text", []), data.get("conf", [])):
        t = (text or "").strip()
        try:
            c = float(conf)
        except (TypeError, ValueError):
            c = -1.0
        if t and c >= 0:
            words.append(t)
            confs.append(c)

    if not words:
        return "", 0.0
    return " ".join(words), sum(confs) / len(confs) / 100.0


def split_header_document(
    page_paths: list[str],
    header_band_pct: float,
) -> tuple[list[SplitHeaderPageOut], list[str]]:
    """OCR every page's header band. Returns per-page results + warnings.

    Per-page failures (unreadable image, OCR garbage) become a null raw_id —
    the page lands in the unmatched bucket for manual review, never aborts
    the whole batch.
    """
    pages_out: list[SplitHeaderPageOut] = []
    warnings: list[str] = []

    for i, path in enumerate(page_paths, start=1):
        p = Path(path)
        if not p.exists():
            warnings.append(f"Page {i}: file not found ({path})")
            pages_out.append(SplitHeaderPageOut(
                page_number=i, raw_id=None, confidence=0.0, ocr_text=""))
            continue
        try:
            text, conf = ocr_header_band(path, header_band_pct)
        except RuntimeError:
            raise  # missing tesseract/binary — fail the job loudly
        except Exception as exc:
            warnings.append(f"Page {i}: OCR failed ({exc})")
            pages_out.append(SplitHeaderPageOut(
                page_number=i, raw_id=None, confidence=0.0, ocr_text=""))
            continue

        raw_id, used_label = extract_student_id(text)
        # A match right after an explicit label is more trustworthy than one
        # plucked from arbitrary header text — small bonus, capped at 1.0.
        page_conf = min(1.0, conf + (0.05 if used_label else 0.0))
        pages_out.append(SplitHeaderPageOut(
            page_number=i,
            raw_id=raw_id,
            confidence=round(page_conf, 4),
            ocr_text=text[:500],  # auditable trail, bounded
        ))

    matched = sum(1 for p in pages_out if p.raw_id)
    if matched < len(pages_out):
        warnings.append(
            f"{len(pages_out) - matched} of {len(pages_out)} pages had no readable student id"
        )
    return pages_out, warnings


def register_split_route(app):
    """Attach POST /split-header to the FastAPI app (called from main.py)."""

    @app.post("/split-header", response_model=SplitHeaderResponse, tags=["split"],
              dependencies=[Depends(require_internal_key)])
    async def split_header(req: SplitHeaderRequest):
        """OCR the header band of answer-sheet pages for the bulk split (M6)."""
        if not req.page_paths:
            raise HTTPException(status_code=422, detail="page_paths must not be empty")
        if len(req.page_paths) > 500:
            raise HTTPException(status_code=422, detail="Too many pages in one call (max 500)")

        band = req.header_band_pct if req.header_band_pct is not None else settings.SPLIT_HEADER_BAND_PCT

        try:
            pages, warnings = split_header_document(req.page_paths, band)
        except RuntimeError as exc:
            raise HTTPException(status_code=500, detail=f"Split OCR failed: {exc}")

        logger.info(
            '{"event":"split_header","pages":%d,"readable":%d,"warnings":%d}'
            % (len(pages), sum(1 for p in pages if p.raw_id), len(warnings))
        )
        return SplitHeaderResponse(pages=pages, warnings=warnings)
