"""Unit tests for the cross-service security contracts (spec 14 §4.1)."""

from __future__ import annotations

import datetime

import pytest
from pydantic import ValidationError

from cordia_agent.contracts.security import (
    AuditEventEnvelope,
    ConsentState,
    RedactionPolicy,
)


_CAPTURED_AT = datetime.datetime(2026, 7, 9, 10, 0, 0, tzinfo=datetime.UTC)


def _make_consent(**overrides: object) -> ConsentState:
    """Build a minimal valid ConsentState."""
    kwargs: dict[str, object] = {
        "consent_policy_version": "us-ca-two-party:2026-01",
        "jurisdiction_policy_id": "jp_us_ca",
        "outcome": "granted",
        "recording_allowed": True,
        "captured_at": _CAPTURED_AT,
        "notice_channel": "voice",
    }
    kwargs.update(overrides)
    return ConsentState(**kwargs)


def _make_audit(**overrides: object) -> AuditEventEnvelope:
    """Build a minimal valid AuditEventEnvelope."""
    kwargs: dict[str, object] = {
        "audit_event_id": "audit_01",
        "tenant_id": "tenant_a",
        "correlation_id": "corr_01",
        "actor_type": "operator",
        "actor_id": "ops.jane",
        "action": "operator_replay",
        "target_type": "dead_letter_message",
        "target_id": "dlm_01",
        "decision": "allow",
        "data_classes": ["booking"],
        "occurred_at": _CAPTURED_AT,
    }
    kwargs.update(overrides)
    return AuditEventEnvelope(**kwargs)


@pytest.mark.unit
def test_consent_state_round_trip() -> None:
    consent = _make_consent()
    restored = ConsentState.model_validate(consent.model_dump(mode="json"))
    assert restored == consent
    assert restored.recording_allowed is True


@pytest.mark.unit
def test_consent_state_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        _make_consent(surprise="x")


@pytest.mark.unit
def test_consent_state_rejects_unknown_outcome() -> None:
    """Outcome is closed to the ratified consent_state pg-enum values (spec 12)."""
    with pytest.raises(ValidationError):
        _make_consent(outcome="maybe")


@pytest.mark.unit
def test_consent_state_rejects_naive_datetime() -> None:
    """captured_at must be offset-aware (AwareDatetime)."""
    with pytest.raises(ValidationError):
        _make_consent(captured_at=datetime.datetime(2026, 7, 9, 10, 0, 0))


@pytest.mark.unit
def test_consent_state_all_outcomes_accepted() -> None:
    for outcome in ("granted", "declined", "not_required", "alternative_offered"):
        assert _make_consent(outcome=outcome).outcome == outcome


@pytest.mark.unit
def test_redaction_policy_always_on_defaults() -> None:
    """PAN/CVV/gov-id masking defaults ON; tenant list empty; spec mask token (Inv-12)."""
    policy = RedactionPolicy()
    assert policy.mask_pan is True
    assert policy.mask_cvv is True
    assert policy.mask_government_id is True
    assert policy.configured_sensitive_fields == []
    assert policy.mask_token == "[REDACTED]"


@pytest.mark.unit
def test_redaction_policy_round_trip_with_selectors() -> None:
    policy = RedactionPolicy(configured_sensitive_fields=["member_number"])
    restored = RedactionPolicy.model_validate(policy.model_dump(mode="json"))
    assert restored == policy


@pytest.mark.unit
def test_redaction_policy_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        RedactionPolicy(surprise="x")


@pytest.mark.unit
def test_audit_envelope_round_trip_and_optional_defaults() -> None:
    envelope = _make_audit()
    assert envelope.causation_id is None
    assert envelope.reason_code is None
    restored = AuditEventEnvelope.model_validate(envelope.model_dump(mode="json"))
    assert restored == envelope


@pytest.mark.unit
def test_audit_envelope_rejects_unknown_decision_and_actor_type() -> None:
    with pytest.raises(ValidationError):
        _make_audit(decision="oops")
    with pytest.raises(ValidationError):
        _make_audit(actor_type="robot")


@pytest.mark.unit
def test_audit_envelope_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        _make_audit(surprise="x")
