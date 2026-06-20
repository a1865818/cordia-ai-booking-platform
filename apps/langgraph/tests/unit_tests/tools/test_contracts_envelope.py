"""Unit tests for ToolResultEnvelope and the tool status enums (spec 03)."""

from enum import Enum

import pytest
from pydantic import ValidationError

from cordia_agent.tools.contracts import SCHEMA_VERSION, ToolResultEnvelope
from cordia_agent.tools.status_codes import (
    AvailabilityMode,
    CatalogItemStatus,
    CheckCandidateAvailabilityStatus,
    CheckVerificationStatus,
    FindCustomerContextStatus,
    FindManageableBookingsStatus,
    GetBookingDetailsStatus,
    GetTenantContextStatus,
    RequestHumanHandoffStatus,
    RequestVerificationStatus,
    SubmitBookingCancellationStatus,
    SubmitBookingModificationStatus,
    SubmitConfirmedIntentStatus,
    ValidateBookingChangeStatus,
    ValidateBookingDraftStatus,
)


def _minimal_payload(**overrides: object) -> dict:
    payload = {
        "ok": True,
        "status": "resolved",
        "correlation_id": "c1",
        "schema_version": SCHEMA_VERSION,
    }
    payload.update(overrides)
    return payload


@pytest.mark.unit
def test_envelope_ok_round_trip() -> None:
    envelope = ToolResultEnvelope.model_validate(_minimal_payload())
    assert envelope.ok is True
    assert envelope.status == "resolved"
    assert envelope.correlation_id == "c1"
    assert envelope.schema_version == SCHEMA_VERSION


@pytest.mark.unit
def test_envelope_non_ok_round_trip() -> None:
    envelope = ToolResultEnvelope.model_validate(
        _minimal_payload(
            ok=False,
            status="hold_failed",
            customer_hint="That time just became unavailable.",
            recoverable_fields=["start_time"],
            suggested_action="request_alternative_time",
            data={"detail": "x"},
        )
    )
    assert envelope.ok is False
    assert envelope.status == "hold_failed"
    assert envelope.customer_hint == "That time just became unavailable."
    assert envelope.recoverable_fields == ["start_time"]
    assert envelope.suggested_action == "request_alternative_time"
    assert envelope.data == {"detail": "x"}


@pytest.mark.unit
def test_envelope_rejects_unknown_keys() -> None:
    with pytest.raises(ValidationError):
        ToolResultEnvelope.model_validate(_minimal_payload(unexpected="x"))


@pytest.mark.unit
@pytest.mark.parametrize("missing", ["schema_version", "correlation_id"])
def test_envelope_required_fields(missing: str) -> None:
    payload = _minimal_payload()
    del payload[missing]
    with pytest.raises(ValidationError):
        ToolResultEnvelope.model_validate(payload)


@pytest.mark.unit
def test_envelope_optional_field_defaults() -> None:
    envelope = ToolResultEnvelope.model_validate(_minimal_payload())
    assert envelope.recoverable_fields == []
    assert envelope.customer_hint is None
    assert envelope.suggested_action is None
    assert envelope.data is None


# Parametrized table pinning each status enum's value set to spec 03 §4.3/§4.4.
# Phases 2-3 extend this table with the per-tool status enums.
STATUS_ENUM_CASES: list[tuple[type[Enum], set[str]]] = [
    (AvailabilityMode, {"informational", "live", "held", "confirmed"}),
    (GetTenantContextStatus, {"resolved", "tenant_not_resolved"}),
    (FindCustomerContextStatus, {"found", "multiple", "none"}),
    (CatalogItemStatus, {"ok", "not_found", "catalog_unavailable"}),
    (
        ValidateBookingDraftStatus,
        {
            "valid",
            "minimum_notice_not_met",
            "outside_operating_hours",
            "party_size_unsupported",
            "service_ineligible",
            "field_missing",
            "field_ambiguous",
        },
    ),
    (
        CheckCandidateAvailabilityStatus,
        {"available", "available_pending", "unavailable", "held", "hold_failed"},
    ),
    (
        RequestVerificationStatus,
        {"sent", "already_sent", "rate_limited", "no_verified_destination"},
    ),
    (CheckVerificationStatus, {"granted", "pending", "failed", "expired"}),
    (
        SubmitConfirmedIntentStatus,
        {
            "accepted",
            "duplicate_accepted",
            "idempotency_conflict",
            "confirmation_invalid",
            "policy_failed",
            "validation_failed",
        },
    ),
    (RequestHumanHandoffStatus, {"created", "existing", "routing_unavailable"}),
    (FindManageableBookingsStatus, {"found", "multiple", "none"}),
    (GetBookingDetailsStatus, {"ok", "requires_verification", "not_found"}),
    (
        ValidateBookingChangeStatus,
        {"valid", "policy_failed", "stale_version", "capacity_unavailable"},
    ),
    (
        SubmitBookingModificationStatus,
        {
            "accepted",
            "duplicate_accepted",
            "idempotency_conflict",
            "stale_version",
            "verification_required",
            "confirmation_invalid",
        },
    ),
    (
        SubmitBookingCancellationStatus,
        {
            "accepted",
            "already_cancelled",
            "duplicate_accepted",
            "idempotency_conflict",
            "stale_version",
            "verification_required",
        },
    ),
]


@pytest.mark.unit
@pytest.mark.parametrize("enum_cls, expected", STATUS_ENUM_CASES)
def test_status_enum_values_match_spec(enum_cls: type[Enum], expected: set[str]) -> None:
    assert {m.value for m in enum_cls} == expected
