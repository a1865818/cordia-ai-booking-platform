"""canon.v1 canonical request hashing (spec 04 §5, Inv-2/Inv-3).

The agent and the Booking API must derive **the same** hash from the same
canonical payload: the API re-canonicalizes ``revision.canonical_payload``
server-side and rejects the submission when
``confirmation.intent_payload_hash`` does not match (spec 04 §8.8, Inv-1).
This module is therefore a deliberate port of
``Cordia.Infrastructure.Services.CanonicalRequestHasher`` — every rule below
mirrors a rule there, including the JSON escaping, and the two are pinned
together by the shared fixtures in
``tests/unit_tests/test_canonical_hash.py`` and
``apps/api/test/Cordia.Tests/UnitTests/Idempotency/CanonicalHashCrossLanguageParityTests.cs``.

Algorithm (applied recursively):

* **Object** — omit properties whose value is ``None`` or canonicalizes to
  ``None``/``{}``/``[]``; NFC-normalize keys; sort keys by code point.
* **Array** — canonicalize every element (``None`` elements are KEPT as JSON
  ``null``, unlike object properties), then sort: arrays of objects by
  ``item_id`` then ``modifier_id`` then full canonical JSON; anything else by
  canonical JSON.
* **String** — an ISO-8601 date-time carrying an offset becomes UTC
  ``yyyy-MM-ddTHH:mm:ss.fffZ`` with sub-millisecond precision TRUNCATED (never
  rounded, so the canonical form cannot cross a second boundary). Any other
  string is NFC-normalized, trimmed, and internal whitespace runs collapse to a
  single space. Case is preserved.
* **Number** — integral values render as a canonical decimal integer; others as
  the shortest round-trip decimal without trailing zeros.
* **Encoding** — compact JSON, escaped exactly as .NET's
  ``JavaScriptEncoder.Default`` does, UTF-8, then SHA-256 → lowercase hex.

Known asymmetries, both benign for payloads this agent produces:

* Timestamp detection uses :meth:`datetime.datetime.fromisoformat`, which is
  stricter than .NET's ``DateTimeOffset.TryParse``. Pydantic
  ``model_dump(mode="json")`` always emits ISO-8601, so every timestamp the
  agent submits is recognised by both sides.
* Sorting compares Python code points where .NET compares UTF-16 code units;
  these differ only for supplementary-plane characters inside sort keys.
"""

from __future__ import annotations

import hashlib
import re
import unicodedata
from datetime import UTC, datetime
from decimal import Decimal
from typing import Any


#: Canonicalizer version label stored alongside the hash (spec 04 §5).
CANONICALIZER_VERSION = "canon.v1"

_WHITESPACE_RUN = re.compile(r"\s+")

#: Characters .NET's JavaScriptEncoder.Default renders with a short escape.
_SHORT_ESCAPES = {
    "\\": "\\\\",
    "\b": "\\b",
    "\t": "\\t",
    "\n": "\\n",
    "\f": "\\f",
    "\r": "\\r",
}

#: Printable ASCII that JavaScriptEncoder.Default still escapes as \uXXXX.
_FORCED_UNICODE_ESCAPES = frozenset("\"&'+<>`")


class _Number:
    """A pre-rendered canonical JSON number.

    Carrying the rendered text (rather than an ``int``/``float``) keeps the
    canonical form byte-exact and independent of Python's own repr rules.
    """

    __slots__ = ("text",)

    def __init__(self, text: str) -> None:
        self.text = text


def canonical_payload_hash(payload: Any) -> str:
    """Return the canon.v1 SHA-256 hex digest of *payload*.

    Args:
        payload: A Pydantic model exposing ``model_dump``, or a plain
            JSON-compatible value. ``None`` hashes as the empty object, matching
            the API's null-payload behaviour.

    Returns:
        A 64-character lowercase hex digest.
    """
    return hashlib.sha256(canonical_json(payload).encode("utf-8")).hexdigest()


def canonical_json(payload: Any) -> str:
    """Return the canon.v1 canonical JSON string for *payload*.

    Exposed separately from :func:`canonical_payload_hash` so parity tests can
    diff the canonical form itself rather than an opaque digest.

    Args:
        payload: A Pydantic model exposing ``model_dump``, or a plain
            JSON-compatible value.

    Returns:
        The compact canonical JSON text that is hashed.
    """
    dump = getattr(payload, "model_dump", None)
    raw = dump(mode="json") if callable(dump) else payload

    if raw is None:
        return "{}"

    canonical = _canonicalize(raw)
    if canonical is None:
        return "{}"
    return _render(canonical)


# ---------------------------------------------------------------------------
# Canonicalization
# ---------------------------------------------------------------------------


def _canonicalize(node: Any) -> Any:
    """Recursively canonicalize *node*; ``None`` means 'JSON null'."""
    if node is None:
        return None
    if isinstance(node, bool):
        return node
    if isinstance(node, int | float | Decimal):
        return _canonicalize_number(node)
    if isinstance(node, str):
        return _canonicalize_string(node)
    if isinstance(node, dict):
        return _canonicalize_object(node)
    if isinstance(node, list | tuple):
        return _canonicalize_array(node)
    raise TypeError(f"canon.v1 cannot canonicalize {type(node).__name__}")


def _canonicalize_object(obj: dict[Any, Any]) -> dict[str, Any]:
    """Drop empty/None members, NFC the keys, and sort by code point."""
    normalized = [
        (unicodedata.normalize("NFC", str(key)), value) for key, value in obj.items()
    ]

    result: dict[str, Any] = {}
    for key, value in sorted(normalized, key=lambda pair: pair[0]):
        if value is None:
            continue
        canonical = _canonicalize(value)
        if canonical is None:
            continue
        # Empty optional collections must not change the hash (spec 04 §4.2).
        if isinstance(canonical, dict | list) and not canonical:
            continue
        result[key] = canonical
    return result


def _canonicalize_array(arr: list[Any] | tuple[Any, ...]) -> list[Any]:
    """Canonicalize each element, then sort into a deterministic order."""
    items = [_canonicalize(element) for element in arr]

    if items and all(isinstance(item, dict) for item in items):
        return sorted(
            items,
            key=lambda item: (
                _natural_key(item.get("item_id")),
                _natural_key(item.get("modifier_id")),
                _render(item),
            ),
        )

    return sorted(items, key=_render)


def _natural_key(node: Any) -> str:
    """Render a natural-key field (``item_id``/``modifier_id``) for sorting."""
    if node is None:
        return ""
    if isinstance(node, str):
        return node
    return _render(node)


def _canonicalize_string(raw: str) -> str:
    """Normalize a timestamp, or NFC/trim/collapse an ordinary string."""
    timestamp = _try_normalize_timestamp(raw)
    if timestamp is not None:
        return timestamp

    return _WHITESPACE_RUN.sub(" ", unicodedata.normalize("NFC", raw).strip())


def _try_normalize_timestamp(raw: str) -> str | None:
    """Return the canonical UTC millisecond form, or ``None`` if not a timestamp."""
    separator = raw.find("T")
    if separator < 0:
        return None
    if not any(marker in raw[separator + 1 :] for marker in ("Z", "z", "+", "-")):
        return None

    try:
        parsed = datetime.fromisoformat(raw)
    except ValueError:
        return None

    if parsed.tzinfo is None:
        return None

    utc = parsed.astimezone(UTC)
    milliseconds = utc.microsecond // 1000
    return f"{utc.strftime('%Y-%m-%dT%H:%M:%S')}.{milliseconds:03d}Z"


def _canonicalize_number(value: int | float | Decimal) -> _Number:
    """Render a number so 500, 500.0 and 5e2 all canonicalize identically."""
    decimal_value = value if isinstance(value, Decimal) else Decimal(str(value))

    if decimal_value == decimal_value.to_integral_value():
        return _Number(str(int(decimal_value)))

    return _Number(format(decimal_value.normalize(), "f"))


# ---------------------------------------------------------------------------
# Rendering — byte-compatible with System.Text.Json's default encoder
# ---------------------------------------------------------------------------


def _render(node: Any) -> str:
    """Render a canonicalized tree as compact JSON."""
    if node is None:
        return "null"
    if node is True:
        return "true"
    if node is False:
        return "false"
    if isinstance(node, _Number):
        return node.text
    if isinstance(node, str):
        return _escape(node)
    if isinstance(node, dict):
        members = ",".join(f"{_escape(k)}:{_render(v)}" for k, v in node.items())
        return "{" + members + "}"
    if isinstance(node, list):
        return "[" + ",".join(_render(item) for item in node) + "]"
    raise TypeError(f"canon.v1 cannot render {type(node).__name__}")


def _escape(text: str) -> str:
    """Escape *text* exactly as .NET's ``JavaScriptEncoder.Default`` does.

    That encoder emits ``\\uXXXX`` with UPPERCASE hex digits, escapes the
    HTML-sensitive set (``" & ' + < > `` and backtick) plus everything outside
    printable ASCII, and encodes supplementary-plane characters as a UTF-16
    surrogate pair.
    """
    out = ['"']
    for char in text:
        short = _SHORT_ESCAPES.get(char)
        if short is not None:
            out.append(short)
            continue

        code_point = ord(char)
        if char in _FORCED_UNICODE_ESCAPES or code_point < 0x20 or code_point >= 0x7F:
            if code_point > 0xFFFF:
                offset = code_point - 0x10000
                high = 0xD800 + (offset >> 10)
                low = 0xDC00 + (offset & 0x3FF)
                out.append(f"\\u{high:04X}\\u{low:04X}")
            else:
                out.append(f"\\u{code_point:04X}")
            continue

        out.append(char)
    out.append('"')
    return "".join(out)
