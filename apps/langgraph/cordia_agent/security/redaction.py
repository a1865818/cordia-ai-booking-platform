"""Pre-emit redaction filter (spec 14 §3.2/§6, Inv-12).

Masks PAN, CVV-in-context, and government identifiers in free text, plus
tenant-configured sensitive fields in structured payloads, BEFORE anything
leaves the model boundary into a checkpoint, queue message, trace, or log.

Precision rules:
- PAN detection requires 13-19 digits (separator-tolerant) AND a passing Luhn
  check — a random 16-digit reference number is NOT masked.
- CVV detection requires a cvv/cvc/security-code context word — a bare 3-digit
  party size is NOT masked.
- PAN/CVV masking is always-on: a policy cannot disable it (Inv-12); only
  government-id masking and the configured-field list are policy-controlled.

Detectors are precompiled once at import; per-call work is regex substitution
only (spec 14 §7 latency budget: < 5 ms per turn-emit).
"""

from __future__ import annotations

import re
from typing import Any

from pydantic import BaseModel

from cordia_agent.contracts.security import RedactionPolicy


_DEFAULT_POLICY = RedactionPolicy()

# Any contiguous run of >= 13 digits allowing single space/dash separators.
# Runs LONGER than 19 digits are still candidates: a PAN concatenated with
# adjacent digits must not escape masking (fail closed, Inv-12).
_PAN_CANDIDATE = re.compile(r"\d(?:[ -]?\d){12,}")

# CVV/CVC/security code followed (optionally compactly, e.g. "cvv123") by 3-4
# digits — no word boundary required between the keyword and the digits.
_CVV_IN_CONTEXT = re.compile(
    r"(?i)\b(cvv2?|cvc|security code)([\s:=-]*(?:is|was|number|no\.?)?[\s:=-]*)(\d{3,4})\b"
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


def _contains_pan(digits: str) -> bool:
    """True iff *digits* is, or contains, a Luhn-valid 13-19 digit window.

    A run of exactly 13-19 digits is a PAN iff it passes Luhn. A LONGER run is
    treated as a PAN when ANY 13-19 digit window inside it passes Luhn — the
    fail-closed reading of Inv-12 (mask a long digit blob that may embed a
    card number rather than let it through).
    """
    n = len(digits)
    if n <= 19:
        return _luhn_valid(digits)
    for size in range(19, 12, -1):
        for start in range(n - size + 1):
            if _luhn_valid(digits[start : start + size]):
                return True
    return False


def _mask_pans(text: str, token: str) -> str:
    """Mask separator-tolerant digit runs that are/contain a Luhn-valid PAN (Inv-12)."""

    def _replace(match: re.Match[str]) -> str:
        digits = re.sub(r"[ -]", "", match.group(0))
        if _contains_pan(digits):
            return token
        return match.group(0)

    return _PAN_CANDIDATE.sub(_replace, text)


def redact_text(text: str, policy: RedactionPolicy | None = None) -> str:
    """Return *text* with PAN/CVV (always) and government ids (per policy) masked.

    Args:
        text: The free text to scrub (transcript, log line, hint).
        policy: Tenant redaction policy; defaults to the always-on baseline.

    Returns:
        The scrubbed text; the input string is never mutated.
    """
    effective = policy or _DEFAULT_POLICY
    token = effective.mask_token

    # Fast path: nothing to mask can exist with fewer than 3 digits (the
    # minimum for CVV); skips the regex sweeps for the typical digitless turn.
    if sum(ch.isdigit() for ch in text) < 3:
        return text

    # PAN and CVV masking are always-on regardless of policy flags (Inv-12).
    out = _mask_pans(text, token)
    out = _CVV_IN_CONTEXT.sub(lambda m: f"{m.group(1)}{m.group(2)}{token}", out)

    if effective.mask_government_id:
        out = _SSN.sub(token, out)
        out = _TFN.sub(token, out)

    return out


def redact_payload(value: Any, policy: RedactionPolicy | None = None) -> Any:
    """Return a scrubbed copy of a structured payload (dict/list/model/scalar).

    Values under keys named in ``policy.configured_sensitive_fields`` are
    replaced wholesale with the mask token; every string value additionally
    passes through :func:`redact_text`. The input is never mutated.
    """
    effective = policy or _DEFAULT_POLICY
    sensitive = set(effective.configured_sensitive_fields)

    def _walk(node: Any) -> Any:
        if isinstance(node, BaseModel):
            return _walk(node.model_dump(mode="json"))
        if isinstance(node, dict):
            return {
                key: (effective.mask_token if key in sensitive else _walk(item))
                for key, item in node.items()
            }
        if isinstance(node, list):
            return [_walk(item) for item in node]
        if isinstance(node, tuple):
            return tuple(_walk(item) for item in node)
        if isinstance(node, str):
            return redact_text(node, effective)
        return node

    return _walk(value)
