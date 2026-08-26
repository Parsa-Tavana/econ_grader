"""Hardened JSON extraction/repair for LLM grader output.

Model responses are occasionally malformed (unescaped inner quotes, trailing
commas, stray prose around the JSON). A bare json.loads failure must not cost
an otherwise-good grading run, so every caller funnels through this module:

  1. plain parse of the cleaned text
  2. strip markdown fences
  3. extract the outer {...} block
  4. strip trailing commas
  5. escape unescaped inner quotes inside string values   <- the live
     2026-08-26 incident: "reasoning": "he said "demand"" killed a whole run
  6. `json-repair` library as last resort

parse_json_hardened() returns (parsed_dict_or_None, error_or_None) — it never
raises. The optional json-repair dependency degrades gracefully if missing.
"""
from __future__ import annotations

import json
import logging
import re
from typing import Any

logger = logging.getLogger(__name__)

try:
    import json_repair as _json_repair_lib
except ImportError:  # pragma: no cover - optional dependency
    _json_repair_lib = None

_FENCED_RE = re.compile(r"^```[a-zA-Z0-9_-]*\s*\n(.*)\n?```\s*$", re.DOTALL)


def _strip_fences(text: str) -> str:
    """Remove a single surrounding markdown code fence, if present."""
    stripped = text.strip()
    m = _FENCED_RE.match(stripped)
    if m:
        return m.group(1).strip()
    # Unbalanced fence (model opened ``` and never closed it)
    lines = stripped.splitlines()
    if lines and lines[0].startswith("```"):
        return "\n".join(lines[1:]).strip()
    return stripped


def _extract_braces(text: str) -> str | None:
    """Return the text from the first '{' to the last '}', or None."""
    start = text.find("{")
    end = text.rfind("}")
    if start >= 0 and end > start:
        return text[start:end + 1]
    return None


def _strip_trailing_commas(text: str) -> str | None:
    """Remove commas directly before } or ] — only outside string literals.

    Returns None when there is nothing to strip (no comma precedes a closer).
    """
    changed = False
    out: list[str] = []
    in_string = False
    escaped = False
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        if in_string:
            out.append(ch)
            if escaped:
                escaped = False
            elif ch == "\\":
                escaped = True
            elif ch == '"':
                in_string = False
            i += 1
            continue
        if ch == '"':
            in_string = True
            out.append(ch)
            i += 1
            continue
        if ch == ",":
            j = i + 1  # look ahead past whitespace to the next significant char
            while j < n and text[j] in " \t\r\n":
                j += 1
            if j < n and text[j] in "}]":
                changed = True
                i += 1  # drop the trailing comma
                continue
        out.append(ch)
        i += 1
    return "".join(out) if changed else None


def _escape_inner_quotes(text: str) -> str | None:
    """Escape double-quotes that sit *inside* string values, so json.loads works.

    Walks the text tracking string state. On a '"' while in-string, treat it as
    the CLOSING quote only if the next non-whitespace char is structural
    (, } ] : ) or end-of-input; otherwise it's an unescaped inner quote and gets
    backslash-escaped. Returns None when no quote was rewritten.
    """
    changed = False
    out: list[str] = []
    in_string = False
    escaped = False
    i = 0
    n = len(text)

    def next_significant(idx: int) -> str:
        j = idx + 1
        while j < n and text[j] in " \t\r\n":
            j += 1
        return text[j] if j < n else ""

    while i < n:
        ch = text[i]
        if not in_string:
            out.append(ch)
            if ch == '"':
                in_string = True
            i += 1
            continue
        if escaped:
            out.append(ch)
            escaped = False
            i += 1
            continue
        if ch == "\\":
            out.append(ch)
            escaped = True
            i += 1
            continue
        if ch == '"':
            nxt = next_significant(i)
            if nxt in (",", "}", "]", ":") or nxt == "":
                in_string = False
                out.append(ch)
            else:
                out.append('\\"')
                changed = True
            i += 1
            continue
        out.append(ch)
        i += 1

    return "".join(out) if changed else None


def parse_json_hardened(raw_text: str) -> tuple[dict[str, Any] | None, str | None]:
    """Parse model output into a dict, applying staged repairs.

    Returns (parsed, error): exactly one of the two is non-None. Never raises.
    """
    if not raw_text or not raw_text.strip():
        return None, "Empty response text"

    cleaned = _strip_fences(raw_text)

    candidates: list[str] = [cleaned]

    brace_block = _extract_braces(cleaned)
    if brace_block is not None:
        candidates.append(brace_block)

        tc = _strip_trailing_commas(brace_block)
        if tc is not None:
            candidates.extend([tc, _escape_inner_quotes(tc)])

        qf = _escape_inner_quotes(brace_block)
        if qf is not None:
            candidates.extend([qf, _strip_trailing_commas(qf)])

    seen: set[str] = set()
    for candidate in candidates:
        if candidate in seen:
            continue
        seen.add(candidate)
        try:
            data = json.loads(candidate)
        except (json.JSONDecodeError, ValueError):
            continue
        if isinstance(data, dict):
            return data, None

    if _json_repair_lib is not None:
        source = brace_block if brace_block is not None else cleaned
        try:
            data = _json_repair_lib.loads(source)
        except Exception:
            data = None
        if isinstance(data, dict):
            logger.info('{"event":"json_repaired_by_library"}')
            return data, None

    return None, f"JSON decode failed after all repair attempts: {raw_text[:200]!r}"
