"""Unit tests for the deterministic read-back renderer (02-I, §8.6 / Inv-16)."""

from __future__ import annotations

import pytest

from cordia_agent.contracts.operation_status import NormalizedOperationStatus
from cordia_agent.readback.renderer import render_read_back, status_wording
from cordia_agent.state import (
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
    TriState,
)
from cordia_agent.tools.status_codes import AvailabilityMode


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _make_draft(**request_overrides: object) -> BookingDraft:
    """Build a minimal valid BookingDraft with optional request overrides."""
    req_kwargs: dict[str, object] = {"operation": OperationType.create}
    req_kwargs.update(request_overrides)
    return BookingDraft(
        tenant_id="tenant_test",
        session_id="sess_test",
        schema_version="1.0",
        tenant_policy_version="v1",
        request=BookingRequest(**req_kwargs),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


# ---------------------------------------------------------------------------
# Determinism tests
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_render_read_back_deterministic_text() -> None:
    """Same draft+mode → byte-identical text (called twice)."""
    draft = _make_draft(service_id="svc_1", location_id="loc_1", party_size=2)
    result1 = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    result2 = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    assert result1.text == result2.text


@pytest.mark.unit
def test_render_read_back_deterministic_version() -> None:
    """Same draft+mode → byte-identical read_back_version (sha256 hash, called twice)."""
    draft = _make_draft(service_id="svc_1", location_id="loc_1", party_size=2)
    result1 = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    result2 = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    assert result1.read_back_version == result2.read_back_version


@pytest.mark.unit
def test_render_read_back_different_mode_different_version() -> None:
    """Different mode → different read_back_version (mode is part of the hash)."""
    draft = _make_draft(service_id="svc_1", party_size=2)
    r_info = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    r_held = render_read_back(draft, AvailabilityMode.HELD)
    assert r_info.read_back_version != r_held.read_back_version


@pytest.mark.unit
def test_render_read_back_different_draft_different_version() -> None:
    """Different draft → different read_back_version."""
    draft_a = _make_draft(service_id="svc_1")
    draft_b = _make_draft(service_id="svc_2")
    r_a = render_read_back(draft_a, AvailabilityMode.INFORMATIONAL)
    r_b = render_read_back(draft_b, AvailabilityMode.INFORMATIONAL)
    assert r_a.read_back_version != r_b.read_back_version


# ---------------------------------------------------------------------------
# Mode wording tests (§6.5)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_mode_wording_informational() -> None:
    """INFORMATIONAL mode produces the §6.5 'appears available' phrase."""
    draft = _make_draft()
    result = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    assert "appears available" in result.text


@pytest.mark.unit
def test_mode_wording_live() -> None:
    """LIVE mode produces the §6.5 'available right now' phrase."""
    draft = _make_draft()
    result = render_read_back(draft, AvailabilityMode.LIVE)
    assert "available right now" in result.text


@pytest.mark.unit
def test_mode_wording_held() -> None:
    """HELD mode produces the §6.5 'hold that time' phrase."""
    draft = _make_draft()
    result = render_read_back(draft, AvailabilityMode.HELD)
    assert "hold" in result.text.lower()


@pytest.mark.unit
def test_mode_wording_informational_no_confirmed() -> None:
    """INFORMATIONAL read-back must NOT contain the word 'confirmed' (Inv-16)."""
    draft = _make_draft(service_id="svc_1", party_size=2)
    result = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    assert "confirmed" not in result.text.lower(), (
        f"Inv-16: 'confirmed' must not appear in INFORMATIONAL read-back;\n{result.text}"
    )


@pytest.mark.unit
def test_mode_wording_live_no_confirmed() -> None:
    """LIVE read-back must NOT contain the word 'confirmed' (Inv-16)."""
    draft = _make_draft(service_id="svc_1", party_size=2)
    result = render_read_back(draft, AvailabilityMode.LIVE)
    assert "confirmed" not in result.text.lower(), (
        f"Inv-16: 'confirmed' must not appear in LIVE read-back;\n{result.text}"
    )


@pytest.mark.unit
def test_mode_wording_held_no_confirmed() -> None:
    """HELD read-back must NOT contain the word 'confirmed' (Inv-16)."""
    draft = _make_draft(service_id="svc_1", party_size=2)
    result = render_read_back(draft, AvailabilityMode.HELD)
    assert "confirmed" not in result.text.lower(), (
        f"Inv-16: 'confirmed' must not appear in HELD read-back;\n{result.text}"
    )


# ---------------------------------------------------------------------------
# TriState / None → "not specified"
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_tristate_unknown_renders_not_specified() -> None:
    """TriState.unknown → 'not specified' in the rendered text."""
    draft = _make_draft(service_id=TriState.unknown, party_size=TriState.unknown)
    result = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    assert "not specified" in result.text


@pytest.mark.unit
def test_none_field_renders_not_specified() -> None:
    """None field value → 'not specified' in the rendered text."""
    draft = _make_draft()  # service_id defaults to None
    result = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    assert "not specified" in result.text


# ---------------------------------------------------------------------------
# status_wording tests (Inv-16)
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    "status",
    [
        NormalizedOperationStatus.ACCEPTED,
        NormalizedOperationStatus.IN_PROGRESS,
        NormalizedOperationStatus.AWAITING_USER_ACTION,
        NormalizedOperationStatus.ALTERNATIVES_REQUIRED,
        NormalizedOperationStatus.WAITLISTED,
        NormalizedOperationStatus.REJECTED,
        NormalizedOperationStatus.EXPIRED,
        NormalizedOperationStatus.FAILED,
    ],
)
def test_status_wording_non_booked_no_confirmed(
    status: NormalizedOperationStatus,
) -> None:
    """Non-booked statuses must NOT produce 'confirmed' wording (Inv-16)."""
    wording = status_wording(status)
    assert "confirmed" not in wording.lower(), (
        f"Inv-16: status {status.value!r} must NOT produce 'confirmed' wording; got: {wording!r}"
    )


@pytest.mark.unit
def test_status_wording_booked_is_confirmed() -> None:
    """ONLY booked status yields 'confirmed' / 'booked' wording (Inv-16)."""
    wording = status_wording(NormalizedOperationStatus.BOOKED)
    assert "confirmed" in wording.lower() or "booked" in wording.lower(), (
        f"Inv-16: booked status must produce 'confirmed/booked' wording; got: {wording!r}"
    )


@pytest.mark.unit
@pytest.mark.parametrize("status", list(NormalizedOperationStatus))
def test_status_wording_all_9_statuses_return_str(
    status: NormalizedOperationStatus,
) -> None:
    """status_wording returns a non-empty string for every NormalizedOperationStatus."""
    wording = status_wording(status)
    assert isinstance(wording, str) and len(wording) > 0, (
        f"status_wording({status.value!r}) must return a non-empty string"
    )
