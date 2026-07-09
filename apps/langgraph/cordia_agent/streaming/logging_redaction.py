"""Recursive log redaction for payloads that may carry raw DTMF digits.

String values additionally pass through the spec-14 text redaction filter
(``cordia_agent.security.redaction.redact_text``) so PAN/CVV/government ids
never reach a log line (Inv-12).
"""

from __future__ import annotations

import json
from typing import Any

from pydantic import BaseModel

from cordia_agent.security.redaction import redact_text


# Aligned with spec 14 RedactionPolicy.mask_token.
REDACTION_TOKEN = "[REDACTED]"  # nosec B105

SENSITIVE_DIGIT_FIELD_NAMES = frozenset(
    {
        "digits",
        "raw_digits",
        "dtmf_digits",
        "verification_code",
        "code",
    }
)


def redact_for_logging(value: Any) -> Any:
    """Return a redacted copy safe for logs without mutating caller-owned data."""
    return _redact_value(value)


def redact_for_logging_json(value: Any) -> str:
    """Serialize a redacted payload to JSON for log emission."""
    return json.dumps(redact_for_logging(value), sort_keys=True)


def _redact_value(value: Any) -> Any:
    if isinstance(value, BaseModel):
        return _redact_mapping(value.model_dump(mode="json"))
    if isinstance(value, dict):
        return _redact_mapping(value)
    if isinstance(value, list):
        return [_redact_value(item) for item in value]
    if isinstance(value, tuple):
        return tuple(_redact_value(item) for item in value)
    if isinstance(value, str):
        return redact_text(value)
    return value


def _redact_mapping(mapping: dict[str, Any]) -> dict[str, Any]:
    redacted: dict[str, Any] = {}
    for key, item in mapping.items():
        if key in SENSITIVE_DIGIT_FIELD_NAMES:
            redacted[key] = _redact_sensitive_field(item)
            continue
        redacted[key] = _redact_value(item)
    return redacted


def _redact_sensitive_field(value: Any) -> Any:
    if isinstance(value, str) and value.isdigit():
        return {"redacted": True, "length": len(value)}
    if isinstance(value, int):
        return {"redacted": True}
    return REDACTION_TOKEN
