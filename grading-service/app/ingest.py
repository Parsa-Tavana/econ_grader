"""Ingest pipeline — render/normalize an uploaded document ONCE.

`POST /ingest` consumes an absolute file path (shared volume, same as /grade)
and produces provider-ready page images + extracted text in ONE place:

  PDF            → rendered page images (both formats: PNG 200 DPI legacy +
                   JPEG 150 DPI new) — content-addressed by rendered bytes
  DOCX           → extracted plain text
  XLSX / XLS     → extracted sheet tables as text
  PNG / JPG      → passed through as-is (single-page document)

The endpoint is DETERMINISTIC and IDEMPOTENT: for each requested format it
returns the same content hash for the same input bytes (the hash IS the
identity), so the .NET layer can dedup pages purely on the returned hashes —
an identical figure inside two different PDFs yields identical page bytes and
one stored image.

The response carries NO storage layout — the .NET side owns paths. Python
only hashes and returns bytes; the .NET side writes them under
pages/{sha[0:2]}/{sha}{ext} and registers Artifact rows.
"""
from __future__ import annotations

import base64
import hashlib
import logging

from fastapi import Depends, HTTPException
from fastapi.responses import JSONResponse

from .config import settings
from .schemas import IngestRequest, IngestResponse, IngestPageOut, IngestFormatOut
from .attachments import (
    prepare_attachments, _extract_docx_text, _extract_xlsx_sheets,
    _render_xlsx_as_table, _extract_xls_text, IMAGE_MEDIA,
)
from .internal_auth import require_internal_key

logger = logging.getLogger("grading-service.ingest")


def _hash_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _encode(data: bytes) -> str:
    return base64.b64encode(data).decode()


def _page_dimensions(png_bytes: bytes) -> tuple[int, int]:
    from PIL import Image
    import io
    with Image.open(io.BytesIO(png_bytes)) as im:
        return im.width, im.height


def _png_to_jpeg_bytes(png_bytes: bytes, quality: int) -> bytes:
    from PIL import Image
    import io
    with Image.open(io.BytesIO(png_bytes)) as im:
        # White background so transparency doesn't turn black in JPEG.
        if im.mode in ("RGBA", "LA", "P"):
            im = im.convert("RGBA")
            background = Image.new("RGB", im.size, (255, 255, 255))
            background.paste(im, mask=im.split()[-1])
            im = background
        elif im.mode != "RGB":
            im = im.convert("RGB")
        buf = io.BytesIO()
        im.save(buf, format="JPEG", quality=quality)
        return buf.getvalue()


def ingest_document(
    path: str,
    formats: list[str],
    max_pages: int,
    jpeg_quality: int,
    render_dpi: int = 200,
    page_offset: int = 0,
) -> tuple[list[IngestPageOut], str, int, list[str]]:
    """Convert one document into provider-ready pages + text.

    Returns (pages, text, total_pages, warnings). Pages carry per-format
    base64 payloads keyed by content hash. Raises ValueError on unusable input.

    page_offset skips the first N pages before applying max_pages — the M6
    bulk-split window mechanism: the .NET side ingests a 400-page merged
    PDF in slices so no single response carries ~1 GB of base64. Page
    numbers in the response are ALWAYS source-document positions
    (offset + i), so callers can correlate slices.
    """
    from pathlib import Path

    p = Path(path)
    if not p.exists():
        raise ValueError(f"File not found: {path}")
    ext = p.suffix.lower()

    warnings: list[str] = []
    pages_out: list[IngestPageOut] = []

    if ext == ".pdf":
        from .pdf_render import render_pdf_to_png_bytes, pdf_page_count

        # M6 windowing: total comes from pdfinfo (cheap); ONLY the requested
        # slice is rendered — a 400-page merged PDF ingested in 20-page
        # windows must not re-render all 400 pages per slice.
        total_pages = pdf_page_count(str(p))
        window_start = page_offset
        if page_offset >= total_pages:
            # Window beyond the end → empty result; caller stops paging.
            return [], "", total_pages, []
        page_numbers = list(range(page_offset + 1, min(page_offset + max_pages, total_pages) + 1))
        if page_numbers and page_numbers[-1] < total_pages:
            warnings.append(
                f"Document has {total_pages} pages; this window covered up to page {page_numbers[-1]}"
            )
        png_pages = render_pdf_to_png_bytes(str(p), dpi=render_dpi, page_numbers=page_numbers)

        for i, png in enumerate(png_pages, start=1):
            width, height = _page_dimensions(png)
            formats_out: list[IngestFormatOut] = []
            for fmt in formats:
                if fmt == "png200":
                    png_bytes = png if render_dpi == 200 else None
                    if png_bytes is None:
                        # Re-render at 200 DPI for the legacy path (rare:
                        # ingest runs at 150 DPI only when jpeg150-only).
                        # i is window-local; re-render needs the SOURCE page.
                        png_bytes = render_pdf_to_png_bytes(
                            str(p), dpi=200, page_numbers=[window_start + i])[0]
                        width, height = _page_dimensions(png_bytes)
                    formats_out.append(IngestFormatOut(
                        format="png200", sha256=_hash_bytes(png_bytes),
                        media_type="image/png", data_b64=_encode(png_bytes)))
                elif fmt == "jpeg150":
                    jpeg_bytes = _png_to_jpeg_bytes(png, jpeg_quality)
                    formats_out.append(IngestFormatOut(
                        format="jpeg150", sha256=_hash_bytes(jpeg_bytes),
                        media_type="image/jpeg", data_b64=_encode(jpeg_bytes)))
                else:
                    raise ValueError(f"Unknown ingest format '{fmt}'")
            pages_out.append(IngestPageOut(
                page_number=window_start + i, width=width, height=height, formats=formats_out))

        text = ""
    elif ext in IMAGE_MEDIA:
        data = p.read_bytes()
        sha = _hash_bytes(data)
        width, height = _page_dimensions(data)
        formats_out: list[IngestFormatOut] = []
        for fmt in formats:
            if fmt == "png200":
                formats_out.append(IngestFormatOut(
                    format="png200", sha256=sha, media_type=IMAGE_MEDIA[ext], data_b64=_encode(data)))
            elif fmt == "jpeg150":
                if IMAGE_MEDIA[ext] == "image/jpeg":
                    # Already JPEG — same bytes for the new format.
                    formats_out.append(IngestFormatOut(
                        format="jpeg150", sha256=sha, media_type="image/jpeg", data_b64=_encode(data)))
                else:
                    jpeg_bytes = _png_to_jpeg_bytes(data, jpeg_quality)
                    formats_out.append(IngestFormatOut(
                        format="jpeg150", sha256=_hash_bytes(jpeg_bytes),
                        media_type="image/jpeg", data_b64=_encode(jpeg_bytes)))
            else:
                raise ValueError(f"Unknown ingest format '{fmt}'")
        pages_out.append(IngestPageOut(page_number=1, width=width, height=height, formats=formats_out))
        text = ""
        total_pages = 1
    elif ext == ".docx":
        text = _extract_docx_text(p)
        total_pages = 0
    elif ext == ".xlsx":
        sheets = _extract_xlsx_sheets(p)
        text = _render_xlsx_as_table(sheets, p.name)
        total_pages = 0
    elif ext == ".xls":
        text = _extract_xls_text(p)
        total_pages = 0
    else:
        raise ValueError(
            f"Unsupported file extension '{ext}' — expected PDF, PNG, JPG, DOCX, XLSX or XLS"
        )

    return pages_out, text, total_pages, warnings


def register_ingest_route(app):
    """Attach POST /ingest to the FastAPI app (called from main.py)."""

    @app.post("/ingest", response_model=IngestResponse, tags=["ingest"],
              dependencies=[Depends(require_internal_key)])
    async def ingest(req: IngestRequest):
        """Render/normalize one document ONCE at upload time.

        The .NET layer calls this right after storing an upload blob; the
        returned per-format page hashes are content addresses — identical
        rendered pages across documents dedup naturally on the .NET side.
        """
        formats = req.formats or ["png200", "jpeg150"]
        try:
            pages, text, total_pages, warnings = ingest_document(
                req.path, formats,
                max_pages=req.max_pages or settings.INGEST_MAX_PAGES,
                jpeg_quality=settings.INGEST_JPEG_QUALITY,
                render_dpi=200,  # png200 format is the ground truth render
                page_offset=max(0, req.page_offset or 0),
            )
        except ValueError as exc:
            raise HTTPException(status_code=422, detail=str(exc))
        except RuntimeError as exc:
            raise HTTPException(status_code=500, detail=f"Ingest failed: {exc}")

        logger.info(
            '{"event":"ingest","path":"%s","pages":%d,"formats":"%s","text_chars":%d,"warnings":%d}'
            % (req.path, len(pages), ",".join(formats), len(text), len(warnings))
        )
        return IngestResponse(
            pages=pages,
            text=text,
            total_pages=total_pages,
            warnings=warnings,
        )
