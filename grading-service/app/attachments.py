"""Attachment preparation — turns uploaded files into provider-ready content.

The .NET upload layer accepts PDF/PNG/JPG/DOCX, but providers only accept
images (+ text in the prompt). This module is the single conversion point:

  - PDF  → rendered page PNGs (pdf2image/poppler), one image per page
  - DOCX → extracted plain text (appended to the prompt)
  - PNG/JPG → passed through with the correct media type

Every grader receives (images, extra_text) instead of raw bytes it must
guess the type of. Conversion failures raise — they are surfaced as
validation errors on the run, never silently skipped.
"""
from __future__ import annotations

import logging
import zipfile
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

logger = logging.getLogger(__name__)

# Extensions we understand. Everything else fails loudly.
IMAGE_MEDIA = {".png": "image/png", ".jpg": "image/jpeg", ".jpeg": "image/jpeg"}


@dataclass
class PreparedAttachments:
    """Provider-ready form of all uploaded files for one grading request."""
    answer_images: list[tuple[bytes, str]] = field(default_factory=list)  # (bytes, media_type)
    question_images: list[tuple[bytes, str]] = field(default_factory=list)
    extra_text: str = ""          # DOCX text etc., appended to the prompt
    warnings: list[str] = field(default_factory=list)


def _media_type_for(path: Path) -> Optional[str]:
    return IMAGE_MEDIA.get(path.suffix.lower())


def _render_pdf(path: Path) -> list[bytes]:
    """Render a PDF to page PNGs. Raises RuntimeError with a clear message."""
    from .pdf_render import render_pdf_to_png_bytes
    return render_pdf_to_png_bytes(str(path))


def _extract_docx_text(path: Path) -> str:
    """Extract paragraph text from a .docx using only stdlib (zipfile+xml)."""
    try:
        import re
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


def prepare_attachments(
    answer_paths: list[str],
    question_paths: list[str],
) -> PreparedAttachments:
    """Convert every file to its provider-ready representation.

    Raises ValueError when an answer file cannot be used at all (bad
    extension, unreadable PDF) — the caller turns that into a failed run.
    """
    prep = PreparedAttachments()

    def convert(path_str: str, *, is_answer: bool):
        path = Path(path_str)
        ext = path.suffix.lower()

        if ext in IMAGE_MEDIA:
            item = (path.read_bytes(), IMAGE_MEDIA[ext])
            (prep.answer_images if is_answer else prep.question_images).append(item)

        elif ext == ".pdf":
            pages = _render_pdf(path)
            if not pages:
                raise ValueError(f"PDF '{path.name}' rendered to zero pages")
            target = prep.answer_images if is_answer else prep.question_images
            target.extend((png, "image/png") for png in pages)

        elif ext == ".docx":
            text = _extract_docx_text(path)
            if not text:
                logger.warning('{"event":"docx_empty","file":"%s"}' % path.name)
                prep.warnings.append(f"DOCX '{path.name}' contained no extractable text")
            else:
                header = (
                    f"[Typed document: {path.name}]" if not is_answer
                    else f"[Student typed answer document: {path.name}]"
                )
                prep.extra_text += f"\n\n{header}\n{text}"

        else:
            raise ValueError(
                f"Unsupported file extension '{ext}' ({path.name}) — expected PDF, PNG, JPG or DOCX"
            )

    for p in question_paths:
        convert(p, is_answer=False)
    for p in answer_paths:
        convert(p, is_answer=True)

    return prep
