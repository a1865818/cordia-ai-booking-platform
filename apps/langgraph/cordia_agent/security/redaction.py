"""Pre-emit redaction filter (spec 14 §3.2/§6, Inv-12).

Masks PAN, CVV-in-context, and government identifiers in free text, plus
tenant-configured sensitive fields in structured payloads, BEFORE anything
leaves the model boundary into a checkpoint, queue message, trace, or log.

Precision rules:
- PAN detection masks any 13+ digit run (separator-tolerant) that embeds a
  Luhn-valid 13-19 digit window. This is fail-closed (Inv-12): a card padded
  with adjacent digits cannot escape, at the cost of masking a non-card
  reference number that coincidentally embeds such a window.
- CVV detection requires a cvv/cvc/security-code context word — a bare 3-digit
  party size is NOT masked.
- PAN/CVV masking is always-on: a policy cannot disable it (Inv-12); only
  government-id masking and the configured-field list are policy-controlled.

Detectors are precompiled once at import; per-call work is regex substitution
only (spec 14 §7 latency budget: < 5 ms per turn-emit).
"""

from __future__ import annotations

import re
from typing import TYPE_CHECKING, Any

from pydantic import BaseModel

from cordia_agent.security.policy import get_redaction_policy


if TYPE_CHECKING:
    from cordia_agent.contracts.security import RedactionPolicy


# Any contiguous run of >= 13 digits allowing up to 3 space/dash separators
# between digits — {0,3} (not the former single {0,1}) so a PAN with a doubled
# space or dash still forms one candidate run and cannot escape masking; bounded
# to stay linear. Every such run is window-scanned (see _contains_pan): a PAN
# concatenated with adjacent digits must not escape masking (fail closed, Inv-12).
_PAN_CANDIDATE = re.compile(r"\d(?:[ -]{0,3}\d){12,}")

# CVV/CVC/security(-)code followed (optionally compactly, e.g. "cvv123") by 3-4
# digits — no word boundary required between the keyword and the digits.
# "security[ -]?code" matches "security code", "security-code", "securitycode".
# The middle group is one separator run plus an optional (filler-word + separator
# run): collapsing the former S*(word)?S* — two adjacent stars over the same
# class — to a single leading star removes the O(n^2) backtracking (ReDoS).
# Python `re` has no match timeout, so bounding the shape structurally IS the fix.
_CVV_IN_CONTEXT = re.compile(
    r"(?i)\b(cvv2?|cvc|security[ -]?code)([\s:=-]*(?:(?:is|was|number|no\.?)[\s:=-]*)?)(\d{3,4})\b"
)

# US SSN (###-##-####) and AU TFN (### ### ### with space/dash separators).
_SSN = re.compile(r"\b\d{3}-\d{2}-\d{4}\b")
_TFN = re.compile(r"\b\d{3}[ -]\d{3}[ -]\d{3}\b")


def _luhn_valid(digits: str) -> bool:
    """Return True iff *digits* passes the Luhn checksum (card-number check)."""
    total = 0
    for index, char in enumerate(reversed(digits)):
        value = int(char)
        if index % 2 == 1:
            value *= 2
            if value > 9:
                value -= 9
        total += value
    return total % 10 == 0


# Window-scanning a very long run is O(n·133); past this length we mask
# unconditionally — strictly MORE fail-closed and O(1), and it bounds the CPU
# an adversarial multi-kilodigit paste can consume (measured ~1s per 100k
# digits without the cap).
_UNCONDITIONAL_MASK_DIGITS = 50


def _contains_pan(digits: str) -> bool:
    """True iff *digits* contains a Luhn-valid 13-19 digit window.

    EVERY run of >= 13 digits is window-scanned (sizes 19..13), not just runs
    longer than 19: a PAN padded with adjacent digits (e.g. a 16-digit card
    with one trailing digit) fails a whole-run Luhn check but must still be
    masked (Inv-12 fail-closed — card data must never escape). This
    deliberately over-masks: a non-Luhn 14-19 digit reference number that
    happens to embed a Luhn-valid window is masked too. Runs past
    ``_UNCONDITIONAL_MASK_DIGITS`` mask without scanning (fail-closed + DoS cap).
    """
    n = len(digits)
    if n > _UNCONDITIONAL_MASK_DIGITS:
        return True
    for size in range(min(19, n), 12, -1):
        for start in range(n - size + 1):
            if _luhn_valid(digits[start : start + size]):
                return True
    return False


# PANs are grouped in short chunks when spoken/typed with separators (4-4-4-4,
# 4-6-5, …) — never in chunks longer than 6 digits. A space-separated group
# LONGER than that inside a candidate run signals independent adjacent numbers
# (two phone numbers, a date-time), not one card number, so such runs are
# evaluated per-group instead of merged (precision: two 10-digit phones one
# space apart must not be masked as a fused 20-digit "PAN").
_MAX_PAN_GROUP_DIGITS = 6


def _mask_candidate_run(run: str, token: str) -> str:
    """Mask a single separator-tolerant candidate run (or its sub-groups)."""
    groups = run.split(" ")
    digit_lengths = [len(g.replace("-", "")) for g in groups]
    if len(groups) > 1 and any(
        length > _MAX_PAN_GROUP_DIGITS for length in digit_lengths
    ):
        # Independent adjacent numbers — evaluate each group on its own.
        masked_groups = [
            token if length >= 13 and _contains_pan(group.replace("-", "")) else group
            for group, length in zip(groups, digit_lengths, strict=True)
        ]
        return " ".join(masked_groups)
    digits = run.replace(" ", "").replace("-", "")
    return token if _contains_pan(digits) else run


def _mask_pans(text: str, token: str) -> str:
    """Mask separator-tolerant digit runs that are/contain a Luhn-valid PAN (Inv-12)."""
    return _PAN_CANDIDATE.sub(lambda m: _mask_candidate_run(m.group(0), token), text)


def redact_text(text: str, policy: RedactionPolicy | None = None) -> str:
    """Return *text* with PAN/CVV (always) and government ids (per policy) masked.

    Args:
        text: The free text to scrub (transcript, log line, hint).
        policy: Tenant redaction policy; defaults to the always-on baseline.

    Returns:
        The scrubbed text; the input string is never mutated.
    """
    effective = policy or get_redaction_policy()
    token = effective.mask_token

    # Fast path: nothing to mask can exist with fewer than 3 digits (the
    # minimum for CVV); skips the regex sweeps for the typical digitless turn.
    # Short-circuits on the third digit (mirrors the C# CountAsciiDigits).
    digits_seen = 0
    for ch in text:
        if ch.isdigit():
            digits_seen += 1
            if digits_seen >= 3:
                break
    if digits_seen < 3:
        return text

    # PAN and CVV masking are always-on regardless of policy flags (Inv-12).
    out = _mask_pans(text, token)
    out = _CVV_IN_CONTEXT.sub(lambda m: f"{m.group(1)}{m.group(2)}{token}", out)

    if effective.mask_government_id:
        out = _SSN.sub(token, out)
        out = _TFN.sub(token, out)

    return out


# Control-plane identifier keys are EXEMPT from value-level text redaction:
# they are system-minted (uuid/hex/hash), never customer speech, and masking a
# chance Luhn-valid digit run inside e.g. ``idem_<uuid4hex>`` corrupts the
# durable copy and breaks the Inv-2 pre-submit durability comparison (a ~0.4%
# per-key collision, observed empirically). Keys named in
# ``configured_sensitive_fields`` still mask wholesale regardless.
#
# "_ref" is deliberately NOT a suffix exemption: customer_ref is CLIENT-supplied
# free text (waitlist enrollment body) that lands in queue payloads, so a PAN
# placed there must mask (Inv-12). A typical channel-identity ref (phone/handle)
# is under 13 digits and never a PAN candidate; the rare 13-15 digit E.164
# number that Luhn-coincidentally embeds a card window masks fail-closed —
# accepted, because no consumer parses customer_ref/identity_ref back OUT of a
# sanitized payload. Only the enumerated system-minted ref keys below are
# exempt; identity_ref is deliberately excluded too (it defaults to the
# customer-supplied customer_ref at waitlist enrollment). Kept identical to
# SensitiveDataMask.cs for parity — pinned by the shared payload-key vectors in
# tools/config/redaction-test-vectors.json.
_CONTROL_PLANE_KEY_SUFFIXES = ("_id", "_key", "_hash", "_version")
_CONTROL_PLANE_KEYS = frozenset(
    {
        "id",
        "seq",
        "ts",
        "provider_ref",
        "source_artifact_ref",
        "image_ref",
        "chunk_ref",
        "audio_chunk_ref",
        "channel_ref",
        "audio_ref",
    }
)


def _is_control_plane_key(key: str) -> bool:
    return key in _CONTROL_PLANE_KEYS or key.endswith(_CONTROL_PLANE_KEY_SUFFIXES)


def redact_payload(value: Any, policy: RedactionPolicy | None = None) -> Any:
    """Return a scrubbed copy of a structured payload (dict/list/model/scalar).

    Values under keys named in ``policy.configured_sensitive_fields`` are
    replaced wholesale with the mask token; every OTHER string value passes
    through :func:`redact_text`, except values under control-plane identifier
    keys (``*_id``/``*_key``/``*_hash``/``*_version``, plus an exact-name
    allowlist of system-minted ``*_ref`` keys), which are system-minted and
    must round-trip byte-for-byte. The input is never mutated.
    """
    effective = policy or get_redaction_policy()
    sensitive = set(effective.configured_sensitive_fields)

    def _walk(node: Any) -> Any:
        if isinstance(node, BaseModel):
            return _walk(node.model_dump(mode="json"))
        if isinstance(node, dict):
            return {key: _walk_entry(key, item) for key, item in node.items()}
        if isinstance(node, list):
            return [_walk(item) for item in node]
        if isinstance(node, tuple):
            return tuple(_walk(item) for item in node)
        if isinstance(node, str):
            return redact_text(node, effective)
        # bool is an int subclass in Python — it is never a maskable scalar and
        # must be checked BEFORE int/float so True/False pass through untouched.
        if isinstance(node, bool):
            return node
        if isinstance(node, int | float):
            # Parity with C# SanitizeJson (WalkNode numeric-scalar branch): an
            # unquoted numeric PAN/SSN must not escape this choke point just
            # because it was written as a JSON number (e.g. {"card": 4111111111111111})
            # rather than a string (Inv-12, fail closed). Detect via the textual
            # form — fixed-point for float so a large value never stringifies to
            # scientific notation (str(1e16) == "1e+16") and slip past the digit
            # detector — and when ANYTHING is maskable replace the WHOLE scalar
            # with the token, never a partial "[REDACTED].45" that leaks the
            # fractional digits sitting next to it. The type deliberately changes
            # number -> string, same as the C# lane. Benign numbers (party sizes,
            # epoch timestamps) have no maskable run and pass through byte-identical.
            # (A float above 2**53 has already lost integer precision before it
            # reaches here, so exact byte-parity with the C# raw-JSON-text lane is
            # not possible for such values; masking stays fail-closed regardless.)
            raw = f"{node:f}" if isinstance(node, float) else str(node)
            return effective.mask_token if redact_text(raw, effective) != raw else node
        return node

    def _walk_entry(key: str, item: Any) -> Any:
        if key in sensitive:
            return effective.mask_token
        # Control-plane identifier keys round-trip their SCALAR value byte-for-byte
        # — a string id/hash OR a numeric one (e.g. a numeric seq/ts), matching the
        # C# exemption (childExempt when the child is a JsonValue). Nested structures
        # under such a key are still walked. This keeps the new numeric masking above
        # from corrupting a system-minted numeric identifier.
        if _is_control_plane_key(key) and not isinstance(
            item, BaseModel | dict | list | tuple
        ):
            return item
        return _walk(item)

    return _walk(value)
