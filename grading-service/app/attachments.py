"""Attachment preparation — turns uploaded files into provider-ready content.

The .NET upload layer accepts PDF/PNG/JPG/DOCX/XLSX/XLS, but providers only
accept images (+ text in the prompt). This module is the single conversion point:

  - PDF        → rendered page PNGs (pdf2image/poppler), one image per page
  - DOCX       → extracted plain text (appended to the prompt)
  - XLSX/XLS   → extracted cell text, one block per sheet (appended to the prompt)
  - PNG/JPG    → passed through with the correct media type

Files arrive in three roles and are kept apart so the grader can present each
correctly:
  - answer   → student answer images + typed-answer document text
  - question → question paper images; document text merges into the question
               statement alongside the typed question text
  - rubric   → exam-wide rubric document images + text, consumed by the
               /extract endpoint (grading no longer takes rubric files)

Conversion failures raise — they are surfaced as validation errors on the run,
never silently skipped.
"""
from __future__ import annotations

import logging
import re
import zipfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

logger = logging.getLogger(__name__)

# Extensions we understand. Everything else fails loudly.
IMAGE_MEDIA = {".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg"}


@dataclass
class PreparedAttachments:
    """Provider-ready form of all uploaded files for one grading request.

    Text is split by role so the grader can merge question-document text into
    the question statement while keeping rubric documents separate.
    """
    answer_images: list[tuple[bytes, str]] = field(default_factory=list)  # (bytes, media_type)
    question_images: list[tuple[bytes, str]] = field(default_factory=list)
    rubric_images: list[tuple[bytes, str]] = field(default_factory=list)
    extra_text: str = ""              # student typed answers (DOCX etc.)
    question_extra_text: str = ""     # question paper document text (DOCX/XLSX…)
    rubric_extra_text: str = ""       # rubric document text
    warnings: list[str] = field(default_factory=list)

    def combined_question_text(self, typed_text: str) -> str:
        """Typed question text + extracted question-document text as ONE statement.

        Either side may be empty; the separator keeps the pieces unambiguous.
        """
        parts = [typed_text.strip(), self.question_extra_text.strip()]
        return "\n\n".join(p for p in parts if p)


def _media_type_for(path: Path) -> Optional[str]:
    return IMAGE_MEDIA.get(path.suffix.lower())


def _render_pdf(path: Path) -> list[bytes]:
    """Render a PDF to page PNGs. Raises RuntimeError with a clear message."""
    from .pdf_render import render_pdf_to_png_bytes
    return render_pdf_to_png_bytes(str(path))


def _extract_docx_text(path: Path) -> str:
    """Extract paragraph text from a .docx using only stdlib (zipfile+xml)."""
    try:
        import xml.etree.ElementTree as ET
        ns = {"w": "http://schemas.openxmlformats.org/wordprocessingml/2006/main"}
        with zipfile.ZipFile(path) as z:
            xml_bytes = z.read("word/document.xml")
        root = ET.fromstring(xml_bytes)
        paragraphs = []
        for p in root.iter("{http://schemas.openxmlformats.org/wordprocessingml/2006/main}p"):
            texts = [t.text or "" for t in p.iter("{http://schemas.openxmlformats.org/wordprocessingml/2006/main}t")]
            paragraphs.append("".join(texts))
        # collapse runs of blank paragraphs
        text = "\n".join(paragraphs)
        text = re.sub(r"\n{3,}", "\n\n", text)
        return text.strip()
    except Exception as exc:
        raise RuntimeError(f"DOCX extraction failed for {path.name}: {exc}") from exc

# Cell value types defined by the SpreadsheetML spec (.xlsx).
_XLSX_NS = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
_CELL_RE = re.compile(r"^([A-Z]+)(\d+)$")

def _col_to_index(ref: str) -> int:
    """'A' -> 0, 'B' -> 1, ... used to place cells into columns."""
    idx = 0
    for ch in ref:
        idx = idx * 26 + (ord(ch) - ord("A") + 1)
    return idx - 1

def _extract_xlsx_sheets(path: Path) -> list[tuple[str, list[list[str]]]]:
    """Extract rows of cell text per sheet from a modern .xlsx (stdlib only).

    Inline strings, shared strings and numeric cells are all resolved to text.
    """
    try:
        import xml.etree.ElementTree as ET

        def q(tag: str) -> str:
            return f"{{{_XLSX_NS}}}{tag}"

        shared: list[str] = []
        with zipfile.ZipFile(path) as z:
            if "xl/sharedStrings.xml" in z.namelist():
                sroot = ET.fromstring(z.read("xl/sharedStrings.xml"))
                for si in sroot.findall(q("si")):
                    shared.append("".join(t.text or "" for t in si.iter(q("t"))))

            # Map sheet XML parts to their display names via the workbook manifest.
            names: dict[str, str] = {}
            wb_root = ET.fromstring(z.read("xl/workbook.xml"))
            rel_map = {}
            try:
                rroot = ET.fromstring(z.read("xl/_rels/workbook.xml.rels"))
                for rel in rroot:
                    rid = rel.get("Id") or ""
                    target = rel.get("Target") or ""
                    rel_map[rid] = "xl/" + target.lstrip("/") if not target.startswith("/") else target.lstrip("/")
            except KeyError:
                pass
            for sh in wb_root.find(q("sheets")):
                rid = sh.get("{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id") or ""
                sheet_file = rel_map.get(rid, "")
                if sheet_file:
                    names[sheet_file] = sh.get("name") or "Sheet"

            sheets: list[tuple[str, list[list[str]]]] = []
            sheet_parts = sorted(p for p in z.namelist() if re.fullmatch(r"xl/worksheets/sheet\d+\.xml", p))
            for part in sheet_parts:
                title = names.get(part) or Path(part).stem.replace("sheet", "Sheet ")
                root = ET.fromstring(z.read(part))
                rows_out: list[list[str]] = []
                for row in root.iter(q("row")):
                    cells_by_col: dict[int, str] = {}
                    max_col = -1
                    for c in row.findall(q("c")):
                        ref = c.get("r") or ""
                        m = _CELL_RE.match(ref)
                        col = _col_to_index(m.group(1)) if m else max_col + 1
                        ctype = c.get("t")
                        if ctype == "inlineStr":
                            v = "".join(t.text or "" for t in c.iter(q("t")))
                        elif ctype == "s":
                            vnode = c.find(q("v"))
                            v = shared[int(vnode.text)] if vnode is not None and vnode.text else ""
                        else:
                            vnode = c.find(q("v"))
                            v = (vnode.text or "") if vnode is not None else ""
                        cells_by_col[col] = v.strip()
                        max_col = max(max_col, col)
                    row_cells = [cells_by_col.get(i, "") for i in range(max_col + 1)]
                    if any(cell for cell in row_cells):
                        rows_out.append(row_cells)
                sheets.append((title, rows_out))
            return sheets
    except Exception as exc:
        raise RuntimeError(f"XLSX extraction failed for {path.name}: {exc}") from exc

def _render_xlsx_as_table(sheets: list[tuple[str, list[list[str]]]], name: str) -> str:
    """Render extracted sheets as a compact pipe-table block for the prompt.

    Sheets without data rows are skipped; returns "" when nothing remains.
    """
    blocks = []
    for title, rows in sheets:
        if not rows:
            continue
        lines = [f"--- Sheet: {title} ---"]
        for row in rows:
            lines.append(" | ".join(row))
        blocks.append("\n".join(lines))
    if not blocks:
        return ""
    return f"[Spreadsheet: {name}]\n" + "\n\n".join(blocks)

def _extract_xls_text(path: Path) -> str:
    """Extract text from legacy binary .xls via pandas/xlrd when available.

    The legacy BIFF format has no practical stdlib reader; if the optional
    dependency is missing we say so clearly instead of failing opaquely.
    """
    try:
        import pandas as pd
        frames = pd.read_excel(path, sheet_name=None, header=None, dtype=str)
    except ImportError:
        raise RuntimeError(
            f"Legacy .xls file '{path.name}' cannot be read — save it as .xlsx and re-upload"
        )
    except Exception as exc:
        raise RuntimeError(f"XLS extraction failed for {path.name}: {exc}") from exc

    blocks = []
    for title, df in frames.items():
        df = df.fillna("")
        lines = [f"--- Sheet: {title} ---"]
        for row in df.itertuples(index=False):
            cells = [str(v).strip() for v in row]
            if any(cells):
                lines.append(" | ".join(cells))
        if len(lines) > 1:  # header only => nothing meaningful on this sheet
            blocks.append("\n".join(lines))
    return "\n\n".join(blocks).strip()


def prepare_attachments(
    answer_paths: list[str],
    question_paths: list[str],
    rubric_paths: list[str] | None = None,
) -> PreparedAttachments:
    """Convert every file to its provider-ready representation.

    Raises ValueError when an answer file cannot be used at all (bad
    extension, unreadable PDF) — the caller turns that into a failed run.
    """
    prep = PreparedAttachments()
    rubric_paths = rubric_paths or []

    def convert(path_str: str, *, role: str):
        """role is 'answer', 'question' or 'rubric'."""
        path = Path(path_str)
        ext = path.suffix.lower()

        images_for_role = {
            "answer": prep.answer_images,
            "question": prep.question_images,
            "rubric": prep.rubric_images,
        }[role]

        def append_text(value: str):
            """Append to the per-role text bucket (str is immutable — always
            assign back to the attribute, never `bucket +=`)."""
            if role == "question":
                prep.question_extra_text += value
            elif role == "rubric":
                prep.rubric_extra_text += value
            else:
                prep.extra_text += value

        if ext in IMAGE_MEDIA:
            item = (path.read_bytes(), IMAGE_MEDIA[ext])
            images_for_role.append(item)

        elif ext == ".pdf":
            pages = _render_pdf(path)
            if not pages:
                raise ValueError(f"PDF '{path.name}' rendered to zero pages")
            images_for_role.extend((png, "image/png") for png in pages)

        elif ext == ".docx":
            text = _extract_docx_text(path)
            if not text:
                logger.warning('{"event":"docx_empty","file":"%s"}' % path.name)
                prep.warnings.append(f"DOCX '{path.name}' contained no extractable text")
            else:
                # Question-paper text merges into the question statement itself,
                # so it needs no role header; answer/rubric docs are labeled.
                header = {
                    "answer": f"[Student typed answer document: {path.name}]\n",
                    "rubric": f"[Rubric document: {path.name}]\n",
                    "question": "",
                }[role]
                append_text(f"\n\n{header}{text}")

        elif ext == ".xlsx":
            sheets = _extract_xlsx_sheets(path)
            table = _render_xlsx_as_table(sheets, path.name)
            if not table.strip():
                logger.warning('{"event":"xlsx_empty","file":"%s"}' % path.name)
                prep.warnings.append(f"XLSX '{path.name}' contained no extractable content")
            elif role == "rubric":
                prep.rubric_extra_text += f"\n\n[Rubric spreadsheet: {path.name}]\n{table}"
            else:
                append_text(f"\n\n{table}")

        elif ext == ".xls":
            text = _extract_xls_text(path)
            if not text:
                logger.warning('{"event":"xls_empty","file":"%s"}' % path.name)
                prep.warnings.append(f"XLS '{path.name}' contained no extractable content")
            elif role == "rubric":
                prep.rubric_extra_text += f"\n\n[Rubric spreadsheet: {path.name}]\n{text}"
            else:
                header = f"[Student typed answer document: {path.name}]" if role == "answer" else f"[Spreadsheet: {path.name}]"
                append_text(f"\n\n{header}\n{text}")

        else:
            raise ValueError(
                f"Unsupported file extension '{ext}' ({path.name}) — expected PDF, PNG, JPG, DOCX, XLSX or XLS"
            )

    for p in rubric_paths:
        convert(p, role="rubric")
    for p in question_paths:
        convert(p, role="question")
    for p in answer_paths:
        convert(p, role="answer")

    return prep
