"""PDF -> image rendering with explicit error handling.

Every failure (unreadable PDF, missing poppler, page mapping ambiguity)
becomes an explicit logged error, never a silent skip.
"""
from __future__ import annotations

import logging
import tempfile
from pathlib import Path
from typing import Optional

try:
    from pdf2image import convert_from_path
    PDF2IMAGE_AVAILABLE = True
except ImportError:
    PDF2IMAGE_AVAILABLE = False
    convert_from_path = None  # type: ignore

logger = logging.getLogger(__name__)


def render_pdf_to_png_bytes(
    pdf_path: str,
    dpi: int = 200,
    page_numbers: Optional[list[int]] = None,
) -> list[bytes]:
    """Render PDF pages to PNG byte arrays.

    Returns list[bytes] in page order. Raises on any failure.
    """
    if not PDF2IMAGE_AVAILABLE:
        raise RuntimeError(
            "pdf2image is not installed — cannot render PDFs. "
            "Ensure poppler is in PATH and pip install pdf2image"
        )

    try:
        pages = convert_from_path(
            pdf_path,
            dpi=dpi,
            fmt="png",
            first_page=page_numbers[0] if page_numbers else None,
            last_page=page_numbers[-1] if page_numbers else None,
        )
    except Exception as exc:
        logger.exception("PDF rendering failed for %s", pdf_path)
        raise RuntimeError(f"PDF render failure: {exc}") from exc

    if page_numbers:
        # Validate page numbers
        if max(page_numbers) > len(pages) or min(page_numbers) < 1:
            raise ValueError(
                f"Page numbers {page_numbers} out of range for {len(pages)}-page PDF"
            )
        pages = [pages[i - 1] for i in page_numbers]

    out = []
    for i, page in enumerate(pages):
        import io
        buf = io.BytesIO()
        page.save(buf, format="PNG")
        out.append(buf.getvalue())
        logger.debug("Rendered page %d of %s -> %d bytes", i + 1, pdf_path, len(out[-1]))
    return out


def render_pdf_pages_to_disk(
    pdf_path: str,
    output_dir: str,
    dpi: int = 200,
    page_numbers: Optional[list[int]] = None,
) -> list[str]:
    """Render PDF pages to PNG files on disk, return file paths."""
    Path(output_dir).mkdir(parents=True, exist_ok=True)
    if not PDF2IMAGE_AVAILABLE:
        raise RuntimeError("pdf2image not available")
    
    pages = convert_from_path(pdf_path, dpi=dpi, fmt="png")
    if page_numbers:
        pages = [pages[i - 1] for i in page_numbers]
    
    out_paths = []
    for i, page in enumerate(pages):
        fname = f"page_{i+1:03d}.png"
        fpath = Path(output_dir) / fname
        page.save(fpath, format="PNG")
        out_paths.append(str(fpath))
    return out_paths