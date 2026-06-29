"""Unit tests for HandoffPackage."""

from __future__ import annotations

import pytest
from pydantic import ValidationError

from cordia_agent.models.handoff import HandoffPackage
from cordia_agent.state import (
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
)


def _make_draft() -> BookingDraft:
    """Build a minimal valid BookingDraft."""
    return BookingDraft(
        tenant_id="tenant_1",
        session_id="session_1",
        schema_version="1.0",
        tenant_policy_version="1.0",
        request=BookingRequest(operation=OperationType.create),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


@pytest.mark.unit
def test_handoff_package_builds_with_reason_only() -> None:
    pkg = HandoffPackage(reason="Customer requested human agent.")
    assert pkg.reason == "Customer requested human agent."
    assert pkg.draft_snapshot is None
    assert pkg.source_turn_ids == []
    assert pkg.summary is None


@pytest.mark.unit
def test_handoff_package_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        HandoffPackage(reason="test", surprise="x")


@pytest.mark.unit
def test_handoff_package_accepts_booking_draft_snapshot() -> None:
    draft = _make_draft()
    pkg = HandoffPackage(
        reason="Escalating complex request.",
        draft_snapshot=draft,
        source_turn_ids=["turn_1", "turn_2"],
        summary="Customer wants a party of 10.",
    )
    assert pkg.draft_snapshot is not None
    assert pkg.draft_snapshot.tenant_id == "tenant_1"
    assert pkg.source_turn_ids == ["turn_1", "turn_2"]
    assert pkg.summary == "Customer wants a party of 10."
