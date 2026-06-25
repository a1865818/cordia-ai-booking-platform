"""Unit tests for IntentRevision, Confirmation, and confirmation_binds_revision."""

from __future__ import annotations

from datetime import UTC, datetime

import pytest
from pydantic import ValidationError

from cordia_agent.models.intent import (
    Confirmation,
    IntentRevision,
    confirmation_binds_revision,
)
from cordia_agent.state import BookingRequest, OperationType


_NOW = datetime(2026, 6, 24, 12, 0, 0, tzinfo=UTC)


def _make_revision(**overrides: object) -> IntentRevision:
    """Build a minimal valid IntentRevision."""
    kwargs: dict[str, object] = {
        "intent_id": "intent_1",
        "revision": 1,
        "canonical_payload": BookingRequest(operation=OperationType.create),
        "schema_version": "1.0",
        "tenant_policy_version": "1.0",
        "payload_hash": "abc123",
        "created_at": _NOW,
        "source_session_id": "session_1",
    }
    kwargs.update(overrides)
    return IntentRevision(**kwargs)


def _make_confirmation(**overrides: object) -> Confirmation:
    """Build a minimal valid Confirmation."""
    kwargs: dict[str, object] = {
        "confirmation_id": "conf_1",
        "intent_id": "intent_1",
        "intent_revision": 1,
        "intent_payload_hash": "abc123",
        "identity": "anonymous_session",
        "confirmation_channel": "voice",
        "confirmed_at": _NOW,
        "source_turn_id": "turn_1",
        "read_back_version": "1.0",
        "verification_level": "none",
    }
    kwargs.update(overrides)
    return Confirmation(**kwargs)


@pytest.mark.unit
def test_intent_revision_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        _make_revision(surprise="x")


@pytest.mark.unit
def test_confirmation_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        _make_confirmation(surprise="x")


@pytest.mark.unit
def test_confirmation_binds_revision_true_when_matching() -> None:
    revision = _make_revision()
    confirmation = _make_confirmation()
    assert confirmation_binds_revision(confirmation, revision) is True


@pytest.mark.unit
def test_confirmation_binds_revision_false_when_hash_differs() -> None:
    revision = _make_revision(payload_hash="abc123")
    confirmation = _make_confirmation(intent_payload_hash="different_hash")
    assert confirmation_binds_revision(confirmation, revision) is False


@pytest.mark.unit
def test_confirmation_binds_revision_false_when_revision_differs() -> None:
    revision = _make_revision(revision=1)
    confirmation = _make_confirmation(intent_revision=2)
    assert confirmation_binds_revision(confirmation, revision) is False


@pytest.mark.unit
def test_confirmation_binds_revision_false_when_intent_id_differs() -> None:
    revision = _make_revision(intent_id="intent_1")
    confirmation = _make_confirmation(intent_id="intent_2")
    assert confirmation_binds_revision(confirmation, revision) is False


@pytest.mark.unit
def test_intent_revision_round_trip() -> None:
    revision = _make_revision(prior_revision=0, supersession_reason="corrected address")
    dumped = revision.model_dump()
    restored = IntentRevision.model_validate(dumped)
    assert restored == revision
    assert restored.prior_revision == 0
    assert restored.supersession_reason == "corrected address"


@pytest.mark.unit
def test_confirmation_round_trip() -> None:
    conf = _make_confirmation(disclosures_presented=["terms_v2", "privacy_v1"])
    dumped = conf.model_dump()
    restored = Confirmation.model_validate(dumped)
    assert restored == conf
    assert restored.disclosures_presented == ["terms_v2", "privacy_v1"]
