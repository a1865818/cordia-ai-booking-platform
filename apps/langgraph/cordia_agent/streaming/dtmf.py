"""Masked DTMF capture for sensitive and error-prone numeric fields (spec 01 Inv-12)."""

from __future__ import annotations

import re
from typing import Any, Literal

from pydantic import BaseModel, Field, field_validator


type DtmfFieldKind = Literal["verification_code", "numeric"]
type DtmfCaptureMethod = Literal["dtmf"]

_DIGIT_PATTERN = re.compile(r"^\d+$")
_MASK_CHAR = "*"


class MaskedDtmfSignal(BaseModel):
    """Structured masked DTMF signal — never carries cleartext digits."""

    model_config = {"extra": "forbid"}

    tenant_id: str = Field(description="Opaque tenant scope, propagated not resolved.")
    session_id: str = Field(description="Identifies one call/conversation.")
    turn_id: str = Field(description="Identifies one customer/agent contribution.")
    correlation_id: str = Field(
        description="Correlation id, begins at channel ingress."
    )
    causation_id: str | None = Field(
        default=None, description="Event-lineage parent id."
    )
    field_id: str = Field(
        description="Target field identifier, e.g. verification_code or party_size."
    )
    field_kind: DtmfFieldKind = Field(
        description="Semantic kind of the captured field."
    )
    capture_method: DtmfCaptureMethod = Field(
        description="How the field value was captured."
    )
    masked_value: str = Field(
        description="Display-safe masked representation of captured digits."
    )
    digit_count: int = Field(
        ge=1, description="Number of digits captured, without exposing values."
    )

    @field_validator("masked_value")
    @classmethod
    def masked_value_contains_no_digits(cls, value: str) -> str:
        if any(character.isdigit() for character in value):
            raise ValueError("masked_value must not contain digit characters")
        return value


def resolve_field_kind(field_id: str) -> DtmfFieldKind:
    """Map a field id to the supported DTMF field kind."""
    if field_id == "verification_code":
        return "verification_code"
    return "numeric"


def mask_digits(raw_digits: str) -> tuple[str, int]:
    """Return a masked display value and digit count from raw DTMF input."""
    _validate_raw_digits(raw_digits)
    return _MASK_CHAR * len(raw_digits), len(raw_digits)


def capture_dtmf_signal(
    *,
    tenant_id: str,
    session_id: str,
    turn_id: str,
    correlation_id: str,
    field_id: str,
    raw_digits: str,
    causation_id: str | None = None,
) -> MaskedDtmfSignal:
    """Convert transient raw DTMF digits into a masked structured signal."""
    masked_value, digit_count = mask_digits(raw_digits)
    return MaskedDtmfSignal(
        tenant_id=tenant_id,
        session_id=session_id,
        turn_id=turn_id,
        correlation_id=correlation_id,
        causation_id=causation_id,
        field_id=field_id,
        field_kind=resolve_field_kind(field_id),
        capture_method="dtmf",
        masked_value=masked_value,
        digit_count=digit_count,
    )


def project_masked_field_input(signal: MaskedDtmfSignal) -> dict[str, Any]:
    """Project a masked signal into the spec 03 / spec 08 input stub shape."""
    return {
        "tenant_id": signal.tenant_id,
        "session_id": signal.session_id,
        "turn_id": signal.turn_id,
        "correlation_id": signal.correlation_id,
        "causation_id": signal.causation_id,
        "field_id": signal.field_id,
        "field_kind": signal.field_kind,
        "capture_method": signal.capture_method,
        "masked_value": signal.masked_value,
        "digit_count": signal.digit_count,
    }


def _validate_raw_digits(raw_digits: str) -> None:
    if not raw_digits:
        raise ValueError("raw_digits must not be empty")
    if not _DIGIT_PATTERN.fullmatch(raw_digits):
        raise ValueError("raw_digits must contain only digit characters")
