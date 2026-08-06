"""``canon.v1`` canonical request hasher — Python port (spec 04 §5).

This is a **mirror**, not a second algorithm.  The authoritative implementation is
``apps/api/src/Cordia.Infrastructure/Services/CanonicalRequestHasher.cs``; all three
lanes are pinned to ``tools/config/canon-v1-test-vectors.json``.

Why it matters: ``POST /api/bookings`` requires the caller to supply
``revision.payload_hash`` and ``confirmation.intent_payload_hash``, and the server
recomputes the hash and rejects a mismatch with 422
``ConfirmationHashMismatchException``.  A lane whose hash drifts cannot submit a
booking at all — which is exactly what the previous placeholder implementation
(``sha256`` over ``json.dumps(..., sort_keys=True)``) would have done.

Algorithm (applied recursively):
    Object  — drop ``None`` values and properties whose canonical value is an empty
              object or empty array; NFC-normalise keys; sort keys by UTF-16 code unit.
    String  — NFC, trim, collapse internal whitespace runs to one space, preserve case.
              ISO-8601 date-times **with an offset** become UTC
              ``yyyy-MM-ddTHH:mm:ss.fffZ``, truncated (floored) to milliseconds.
    Number  — 500, 500.0 and 5e2 all canonicalise to ``500``; fractions use the
              shortest round-trip form with no exponent and no trailing zeros.
              Limit (shared with the TypeScript port): a JSON number written with a
              ``.`` or an exponent is parsed to an IEEE-754 double first, so values
              needing more than ~17 significant digits (e.g. ``1e23``) can differ from
              the C# side, which parses the raw text exactly.  Booking payloads carry
              only integer counts and minor-unit amounts, which are exact.
    Array   — canonicalise every element, then sort: arrays of objects by ``item_id``,
              then ``modifier_id``, then the element's full canonical JSON; other
              arrays by that canonical JSON.  ``None`` is **kept** inside arrays.
    Encode  — compact JSON, then SHA-256 → lowercase hex.

Three details are load-bearing and easy to get wrong:

1. The JSON encoding is .NET's default ``JavaScriptEncoder``: every non-ASCII code
   unit and the HTML-sensitive ASCII set become ``\\uXXXX`` with UPPERCASE hex.
   ``json.dumps`` does not produce this.
2. The whitespace class is .NET's ``char.IsWhiteSpace`` — it includes U+0085 and
   excludes U+FEFF, unlike ``str.isspace()``/``str.strip()``.
3. Ordering is by UTF-16 code unit, not by Python code point.  The two disagree for
   astral characters (an emoji sorts before U+FFFD in UTF-16 but after it by code
   point), so sort keys are the UTF-16-BE encoding.
"""

from __future__ import annotations

import hashlib
import re
import unicodedata
from datetime import UTC, datetime, timedelta
from typing import Any, NamedTuple


CANON_VERSION = "canon.v1"


class CanonicalHash(NamedTuple):
    """Versioned canonical hash — mirrors the C# ``CanonicalHash`` record."""

    version: str
    value: str


# .NET's char.IsWhiteSpace: U+0009-U+000D, U+0020, U+0085, plus the Zs/Zl/Zp
# categories.  Deliberately NOT str.isspace()/str.strip(), whose set differs at
# U+0085 and U+FEFF.  Built from code points so no literal control characters
# appear in this source file.
_WHITESPACE = "".join(
    chr(c)
    for c in (
        0x09,
        0x0A,
        0x0B,
        0x0C,
        0x0D,
        0x20,
        0x85,
        0xA0,
        0x1680,
        *range(0x2000, 0x200B),
        0x2028,
        0x2029,
        0x202F,
        0x205F,
        0x3000,
    )
)
_WHITESPACE_RUN = re.compile("[" + "".join(re.escape(c) for c in _WHITESPACE) + "]+")

# ASCII characters .NET's default JavaScriptEncoder escapes even though printable.
_ESCAPED_ASCII = frozenset("\"&'+<>`")

# ISO-8601 date-time carrying an offset.
#
# Month and day accept one or two digits because .NET's DateTimeOffset.TryParse does;
# hour/minute/second must be two digits because it does NOT tolerate a missing leading
# zero there.  The separator is uppercase "T" only: the C# gate is
# IndexOf('T', StringComparison.Ordinal), so a lowercase "t" never reaches TryParse and
# the value stays a plain string.  Anything this rejects is canonicalised as plain text,
# exactly as the server does.
_ISO_WITH_OFFSET = re.compile(
    r"^(\d{4})-(\d{1,2})-(\d{1,2})T(\d{2}):(\d{2})"
    r"(?::(\d{2})(?:\.(\d+))?)?"
    r"(?:([Zz])|([+-])(\d{2})(?::?(\d{2}))?)$"
)

_EXPONENT = re.compile(r"^(-?)(\d+)(?:\.(\d+))?[eE]([+-]?\d+)$")

_OMIT = object()


def _sort_key(text: str) -> bytes:
    """UTF-16 code-unit ordering, matching .NET's ``StringComparison.Ordinal``."""
    return text.encode("utf-16-be", errors="surrogatepass")


#: Control characters System.Text.Json writes as two-character escapes, not ``\\uXXXX``.
#: Verified against the real encoder on net10.0. Emitting ``\\u0008`` where the server emits
#: ``\\b`` produces a different SHA-256, and the submission is rejected with 422.
_SHORT_ESCAPES = {0x08: "\\b", 0x09: "\\t", 0x0A: "\\n", 0x0C: "\\f", 0x0D: "\\r"}


def _encode_string(value: str) -> str:
    """Serialises a string the way .NET's default JavaScriptEncoder does."""
    out = ['"']
    for char in value:
        code = ord(char)
        if char == "\\":
            out.append("\\\\")
        elif code in _SHORT_ESCAPES:
            out.append(_SHORT_ESCAPES[code])
        elif 0x20 <= code <= 0x7E and char not in _ESCAPED_ASCII:
            out.append(char)
        elif code > 0xFFFF:
            # Python strings are code points; .NET escapes per UTF-16 code unit.
            offset = code - 0x10000
            out.append(f"\\u{0xD800 + (offset >> 10):04X}")
            out.append(f"\\u{0xDC00 + (offset & 0x3FF):04X}")
        else:
            out.append(f"\\u{code:04X}")
    out.append('"')
    return "".join(out)


#: .NET's DateTimeOffset offset range is +/-14:00 inclusive; beyond that TryParse fails.
_MAX_OFFSET_MINUTES = 14 * 60


def _try_normalize_timestamp(raw: str) -> str | None:
    """Returns the UTC ``yyyy-MM-ddTHH:mm:ss.fffZ`` form, or ``None`` if not a timestamp."""
    t_index = raw.find("T")
    if t_index < 0:
        return None
    if not any(c in raw[t_index + 1 :] for c in "Zz+-"):
        return None

    # Matched against the TRIMMED value: .NET's DateTimeOffset.TryParse tolerates surrounding
    # whitespace (verified on net10.0), so the server treats " 2026-06-15T18:00:00Z" as a
    # timestamp. Anchoring on the raw string would not, and the hashes would diverge.
    match = _ISO_WITH_OFFSET.match(raw.strip())
    if match is None:
        return None

    year, month, day, hour, minute, second, fraction, _zulu, sign, off_h, off_m = (
        match.groups()
    )

    # The regex's two-digit offset groups have no upper bound, but .NET rejects anything past
    # +/-14:00. Accepting "+15:00" would normalise to UTC while the server kept the raw string.
    if sign is not None and int(off_h) * 60 + int(off_m or 0) > _MAX_OFFSET_MINUTES:
        return None

    # .NET also rejects an offset whose MINUTE component exceeds 59, even when the total is
    # inside +/-14:00 — "+05:75" is 375 minutes but TryParse still fails. timedelta would
    # happily carry the overflow into hours, so without this guard Python normalises to UTC
    # while the server keeps the raw string (corpus vector
    # iso_offset_minute_overflow_is_a_plain_string).
    if sign is not None and int(off_m or 0) > 59:
        return None

    # Truncate (floor) sub-millisecond precision.  Rounding could cross a second
    # boundary and silently merge two distinct instants.
    millis = int((fraction + "000")[:3]) if fraction else 0

    if sign is None:
        offset = timedelta(0)
    else:
        delta = timedelta(hours=int(off_h), minutes=int(off_m or 0))
        offset = -delta if sign == "-" else delta

    try:
        moment = datetime(
            int(year),
            int(month),
            int(day),
            int(hour),
            int(minute),
            int(second or 0),
            millis * 1000,
            tzinfo=UTC,
        )
        # Applying the offset can push the instant past datetime.min/max near year 1 or
        # 9999.  DateTimeOffset.TryParse simply returns false for those, so fall through
        # to plain-string handling rather than raising out of the hasher.
        utc = moment - offset
    except (ValueError, OverflowError):
        return None

    return f"{utc.year:04d}-{utc.month:02d}-{utc.day:02d}T{utc.hour:02d}:{utc.minute:02d}:{utc.second:02d}.{utc.microsecond // 1000:03d}Z"


def _canonicalize_string(raw: str) -> str:
    timestamp = _try_normalize_timestamp(raw)
    if timestamp is not None:
        return timestamp

    normalized = unicodedata.normalize("NFC", raw)
    stripped = normalized.strip(_WHITESPACE)
    return _WHITESPACE_RUN.sub(" ", stripped)


def _without_exponent(text: str) -> str:
    """Expands exponent notation so the canonical form never contains ``e``."""
    match = _EXPONENT.match(text)
    if match is None:
        return text

    sign, int_part, frac_part, exp_text = match.groups()
    frac_part = frac_part or ""
    exponent = int(exp_text)
    digits = int_part + frac_part
    point = len(int_part) + exponent

    if point <= 0:
        result = f"{sign}0.{'0' * -point}{digits}"
    elif point >= len(digits):
        return sign + digits + "0" * (point - len(digits))
    else:
        result = f"{sign}{digits[:point]}.{digits[point:]}"

    return result.rstrip("0").rstrip(".")


def _canonicalize_number(value: int | float) -> str:
    if isinstance(
        value, bool
    ):  # bool is a subclass of int — never reached, but explicit.
        return "true" if value else "false"

    if isinstance(value, int):
        return str(value)

    if value != value or value in (float("inf"), float("-inf")):
        # json.loads accepts the non-standard NaN/Infinity tokens, but they are not valid
        # JSON, so the server rejects such a payload before hashing.  Handled defensively.
        return "0"

    if value.is_integer():
        # -0.0 canonicalises to 0: BigInteger has no negative zero.
        return str(int(value))

    return _without_exponent(repr(value))


class _Node(NamedTuple):
    text: str
    is_object: bool
    item_id: str
    modifier_id: str


def _node(text: str) -> _Node:
    return _Node(text, False, "", "")


def _canonicalize(value: Any) -> Any:
    if value is None:
        return _OMIT
    if isinstance(value, bool):
        return _node("true" if value else "false")
    if isinstance(value, int | float):
        return _node(_canonicalize_number(value))
    if isinstance(value, str):
        return _node(_encode_string(_canonicalize_string(value)))
    if isinstance(value, list | tuple):
        return _canonicalize_array(list(value))
    if isinstance(value, dict):
        return _canonicalize_object(value)
    return _OMIT


def _canonicalize_element(value: Any) -> _Node:
    """Array elements keep JSON ``null`` — only object properties drop it."""
    canonical = _canonicalize(value)
    return _node("null") if canonical is _OMIT else canonical


def _natural_key(
    entries: dict[str, _Node], raw_strings: dict[str, str | None], key: str
) -> str:
    node = entries.get(key)
    if node is None:
        return ""
    raw = raw_strings.get(key)
    return raw if raw is not None else node.text


def _canonicalize_object(source: dict[Any, Any]) -> _Node:
    entries: dict[str, _Node] = {}
    raw_strings: dict[str, str | None] = {}

    for key, raw in source.items():
        canonical = _canonicalize(raw)
        if canonical is _OMIT:
            continue
        if canonical.text in ("{}", "[]"):
            continue

        normalized_key = unicodedata.normalize("NFC", str(key))

        # Two distinct keys can normalise to the same one.  The server's JsonObject.Add
        # throws on that, so silently keeping the last would produce a hash the server can
        # never agree with — fail here, where the message is actionable.
        if normalized_key in entries:
            raise ValueError(
                f"canon.v1: object has two keys that normalise to {normalized_key!r}; "
                "the payload cannot be canonicalised unambiguously."
            )

        entries[normalized_key] = canonical
        raw_strings[normalized_key] = (
            _canonicalize_string(raw) if isinstance(raw, str) else None
        )

    ordered = sorted(entries, key=_sort_key)
    body = ",".join(f"{_encode_string(k)}:{entries[k].text}" for k in ordered)

    return _Node(
        f"{{{body}}}",
        True,
        _natural_key(entries, raw_strings, "item_id"),
        _natural_key(entries, raw_strings, "modifier_id"),
    )


def _canonicalize_array(source: list[Any]) -> _Node:
    items = [_canonicalize_element(v) for v in source]

    if items and all(i.is_object for i in items):
        items.sort(
            key=lambda i: (
                _sort_key(i.item_id),
                _sort_key(i.modifier_id),
                _sort_key(i.text),
            )
        )
    else:
        items.sort(key=lambda i: _sort_key(i.text))

    return _node("[" + ",".join(i.text for i in items) + "]")


def canonical_json(payload: Any) -> str:
    """The canonical JSON encoding — exported so a hash mismatch can be diagnosed as text."""
    if payload is None:
        return "{}"
    canonical = _canonicalize(payload)
    return "{}" if canonical is _OMIT else canonical.text


def canonical_hash(payload: Any) -> CanonicalHash:
    """Computes the ``canon.v1`` hash of an already-JSON-shaped payload."""
    encoded = canonical_json(payload).encode("utf-8")
    return CanonicalHash(CANON_VERSION, hashlib.sha256(encoded).hexdigest())
