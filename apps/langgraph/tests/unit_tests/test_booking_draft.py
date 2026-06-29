"""Unit tests for the booking draft models."""

from datetime import datetime

import pytest
from pydantic import ValidationError

from cordia_agent.state import (
    BookingDraft,
    BookingRequest,
    CaptureMethod,
    Channel,
    CustomerRef,
    FieldProvenance,
    OperationType,
    SourceInfo,
    TriState,
)


def _make_draft(**overrides: object) -> BookingDraft:
    """Build a minimal valid BookingDraft, applying any field overrides."""
    kwargs: dict[str, object] = {
        "tenant_id": "tenant_1",
        "session_id": "session_1",
        "schema_version": "1.0",
        "tenant_policy_version": "1.0",
        "request": BookingRequest(operation=OperationType.create),
        "source": SourceInfo(channel=Channel.voice, language="en-AU"),
    }
    kwargs.update(overrides)
    return BookingDraft(**kwargs)


@pytest.mark.unit
def test_booking_request_rejects_unknown_field() -> None:  # AC1
    with pytest.raises(ValidationError):
        BookingRequest(operation=OperationType.create, surprise="x")


@pytest.mark.unit
def test_booking_request_rejects_naive_datetime() -> None:  # AC3
    with pytest.raises(ValidationError):
        BookingRequest(
            operation=OperationType.create,
            starts_at=datetime(2026, 6, 20, 19, 0),  # naive (no tzinfo)
        )


@pytest.mark.unit
def test_booking_request_rejects_nonpositive_party_size() -> None:  # AC3
    with pytest.raises(ValidationError):
        BookingRequest(operation=OperationType.create, party_size=0)


@pytest.mark.unit
def test_booking_request_accepts_tristate_inline() -> None:  # DQ1=A
    req = BookingRequest(
        operation=OperationType.create,
        service_id="svc_1",
        party_size=TriState.unknown,
        location_id=TriState.declined,
    )
    assert req.service_id == "svc_1"
    assert req.party_size == TriState.unknown
    assert req.location_id == TriState.declined
    assert req.starts_at is None


@pytest.mark.unit
@pytest.mark.parametrize("sentinel", ["unknown", "declined", "absent"])
def test_str_tristate_fields_deserialize_sentinels_as_tristate(sentinel: str) -> None:
    """Raw sentinel strings must validate to TriState, not plain str (DQ1=A).

    service_id/location_id are str-subclass-enum unions; smart-mode would
    downgrade the sentinel strings to plain str. union_mode=left_to_right keeps them.
    """
    req = BookingRequest.model_validate(
        {"operation": "create", "service_id": sentinel, "location_id": sentinel}
    )
    assert isinstance(req.service_id, TriState)
    assert req.service_id is TriState(sentinel)
    assert isinstance(req.location_id, TriState)
    assert req.location_id is TriState(sentinel)


@pytest.mark.unit
def test_str_tristate_fields_keep_real_ids_as_str() -> None:
    """Non-sentinel strings still validate as plain str values (DQ1=A)."""
    req = BookingRequest.model_validate(
        {"operation": "create", "service_id": "svc_1", "location_id": "loc_1"}
    )
    assert req.service_id == "svc_1"
    assert not isinstance(req.service_id, TriState)
    assert req.location_id == "loc_1"
    assert not isinstance(req.location_id, TriState)


@pytest.mark.unit
def test_target_optional_for_create() -> None:  # AC4 / DQ4
    req = BookingRequest(operation=OperationType.create)
    assert req.target_booking_id is None


@pytest.mark.unit
def test_target_required_for_cancel() -> None:  # AC4 / DQ4
    with pytest.raises(ValidationError):
        BookingRequest(operation=OperationType.cancel)


@pytest.mark.unit
def test_booking_draft_rejects_unknown_field() -> None:  # AC1
    with pytest.raises(ValidationError):
        _make_draft(surprise="x")


@pytest.mark.unit
def test_canonical_projection_strips_provenance_keeps_tristate() -> None:  # AC2
    draft = _make_draft(
        customer=CustomerRef(display_name="Ada"),
        request=BookingRequest(
            operation=OperationType.create,
            service_id="svc_1",  # value
            party_size=TriState.unknown,  # unknown
            location_id=TriState.declined,  # declined
            # starts_at omitted -> absent (None)
        ),
        field_provenance={
            "service_id": FieldProvenance(capture_method=CaptureMethod.speech)
        },
    )

    canon = draft.to_canonical()

    assert not hasattr(canon, "field_provenance")
    assert canon.request.service_id == "svc_1"
    assert canon.request.party_size == TriState.unknown
    assert canon.request.location_id == TriState.declined
    assert canon.request.starts_at is None
