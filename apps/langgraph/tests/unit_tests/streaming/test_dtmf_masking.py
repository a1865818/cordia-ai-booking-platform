"""DTMF capture must not become transcript text and must not leak raw digits."""

from __future__ import annotations

import json
from datetime import UTC, datetime

import pytest
from pydantic import BaseModel, ValidationError

from cordia_agent.streaming.dtmf import (
    MaskedDtmfSignal,
    capture_dtmf_signal,
    project_masked_field_input,
)
from cordia_agent.streaming.logging_redaction import (
    redact_for_logging,
    redact_for_logging_json,
)
from cordia_agent.streaming_models import StreamFrameEnvelope, TranscriptFinalFrame


RAW_DIGITS = "123456"
IDENTITY = {
    "tenant_id": "tenant_acme",
    "session_id": "session_42",
    "turn_id": "turn_7",
    "correlation_id": "corr_abc",
    "causation_id": "cause_parent",
}

FROZEN_VERIFICATION_CODE_SAMPLE = {
    "tenant_id": "tenant_acme",
    "session_id": "session_42",
    "turn_id": "turn_7",
    "correlation_id": "corr_abc",
    "causation_id": "cause_parent",
    "field_id": "verification_code",
    "field_kind": "verification_code",
    "capture_method": "dtmf",
    "masked_value": "******",
    "digit_count": 6,
}


@pytest.mark.unit
def test_dtmf_capture_returns_masked_structured_signal_not_stream_frame() -> None:
    """DTMF input becomes a masked signal, not a streaming frame variant."""
    signal = capture_dtmf_signal(
        field_id="verification_code", raw_digits=RAW_DIGITS, **IDENTITY
    )

    assert isinstance(signal, MaskedDtmfSignal)
    assert not isinstance(signal, StreamFrameEnvelope)
    assert not isinstance(signal, TranscriptFinalFrame)


@pytest.mark.unit
def test_dtmf_input_does_not_produce_transcript_final_frame() -> None:
    """Inv-12: DTMF digits must never be surfaced as transcript.final."""
    signal = capture_dtmf_signal(
        field_id="verification_code", raw_digits=RAW_DIGITS, **IDENTITY
    )

    assert TranscriptFinalFrame not in type(signal).__mro__
    assert signal.capture_method == "dtmf"
    assert "transcript" not in repr(signal)


@pytest.mark.unit
@pytest.mark.parametrize(
    "serialized",
    [
        lambda signal: repr(signal),
        lambda signal: str(signal),
        lambda signal: signal.model_dump(),
        lambda signal: signal.model_dump_json(),
    ],
)
def test_raw_digits_are_not_logged_in_signal_representations(
    serialized: object,
) -> None:
    """Raw digits must not appear in repr/str/dump/json for the masked signal."""
    signal = capture_dtmf_signal(
        field_id="verification_code", raw_digits=RAW_DIGITS, **IDENTITY
    )
    output = serialized(signal)  # type: ignore[operator]

    payload = json.dumps(output) if isinstance(output, dict) else str(output)

    assert RAW_DIGITS not in payload


@pytest.mark.unit
def test_round_trip_preserves_masked_metadata_not_cleartext_digits() -> None:
    """Round-trip validation keeps field semantics and masked metadata only."""
    signal = capture_dtmf_signal(field_id="party_size", raw_digits="4", **IDENTITY)
    restored = MaskedDtmfSignal.model_validate(signal.model_dump())

    assert restored == signal
    assert restored.field_id == "party_size"
    assert restored.field_kind == "numeric"
    assert restored.capture_method == "dtmf"
    assert restored.digit_count == 1
    assert restored.masked_value == "*"
    assert RAW_DIGITS not in restored.model_dump_json()


@pytest.mark.unit
def test_frozen_verification_code_sample_matches_spec_stub() -> None:
    """Frozen sample for spec 03 / spec 08 masked verification-code input."""
    signal = capture_dtmf_signal(
        field_id="verification_code", raw_digits=RAW_DIGITS, **IDENTITY
    )

    assert signal.model_dump() == FROZEN_VERIFICATION_CODE_SAMPLE
    assert project_masked_field_input(signal) == FROZEN_VERIFICATION_CODE_SAMPLE


@pytest.mark.unit
@pytest.mark.parametrize("raw_digits", ["", "12a456", "12 456", "12.5"])
def test_capture_rejects_empty_and_non_digit_input(raw_digits: str) -> None:
    with pytest.raises(ValueError):
        capture_dtmf_signal(
            field_id="verification_code", raw_digits=raw_digits, **IDENTITY
        )


@pytest.mark.unit
def test_masked_signal_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        MaskedDtmfSignal(
            **FROZEN_VERIFICATION_CODE_SAMPLE,
            raw_digits=RAW_DIGITS,
        )


@pytest.mark.unit
def test_redacted_logs_do_not_contain_raw_digits_for_dict_payload() -> None:
    payload = {
        "field_id": "verification_code",
        "digit_count": 6,
        "masked_value": "******",
        "raw_digits": RAW_DIGITS,
        "nested": {"code": RAW_DIGITS},
    }

    redacted = redact_for_logging(payload)
    serialized = redact_for_logging_json(payload)

    assert RAW_DIGITS not in serialized
    assert redacted["raw_digits"] == {"redacted": True, "length": 6}
    assert redacted["nested"]["code"] == {"redacted": True, "length": 6}
    assert payload["raw_digits"] == RAW_DIGITS


@pytest.mark.unit
def test_redacted_logs_do_not_contain_raw_digits_for_pydantic_payload() -> None:
    class SensitiveLogPayload(BaseModel):
        field_id: str
        raw_digits: str

    payload = SensitiveLogPayload(field_id="verification_code", raw_digits=RAW_DIGITS)
    redacted = redact_for_logging(payload)
    serialized = redact_for_logging_json(payload)

    assert RAW_DIGITS not in serialized
    assert redacted["raw_digits"] == {"redacted": True, "length": 6}


@pytest.mark.unit
def test_redacted_logs_json_serializes_pydantic_datetime_fields() -> None:
    class SensitiveLogPayload(BaseModel):
        occurred_at: datetime
        raw_digits: str

    occurred_at = datetime(2026, 6, 21, 3, 59, 7, 621642, tzinfo=UTC)
    payload = SensitiveLogPayload(occurred_at=occurred_at, raw_digits=RAW_DIGITS)

    redacted = redact_for_logging(payload)
    serialized = redact_for_logging_json(payload)

    assert isinstance(redacted["occurred_at"], str)
    assert redacted["occurred_at"] == occurred_at.isoformat().replace("+00:00", "Z")
    assert redacted["raw_digits"] == {"redacted": True, "length": 6}
    assert RAW_DIGITS not in serialized
    json.loads(serialized)


@pytest.mark.unit
def test_redacted_logs_preserve_safe_context() -> None:
    payload = {
        "tenant_id": IDENTITY["tenant_id"],
        "session_id": IDENTITY["session_id"],
        "correlation_id": IDENTITY["correlation_id"],
        "field_id": "verification_code",
        "digit_count": 6,
        "masked_value": "******",
        "raw_digits": RAW_DIGITS,
    }

    redacted = redact_for_logging(payload)

    assert redacted["tenant_id"] == IDENTITY["tenant_id"]
    assert redacted["session_id"] == IDENTITY["session_id"]
    assert redacted["correlation_id"] == IDENTITY["correlation_id"]
    assert redacted["field_id"] == "verification_code"
    assert redacted["digit_count"] == 6
    assert redacted["masked_value"] == "******"
