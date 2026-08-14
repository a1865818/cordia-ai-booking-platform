"""Unit tests for pure-read foreground tool I/O models (spec 03 §4.3 / §6.1).

Covers: PolicyValidationResult, OperationStatusView, VerificationGrantView,
VerificationAction value set, canonical-model identity,
and optional view models (TenantContextView, CustomerContextView, CatalogItem).
"""

from __future__ import annotations

import pytest
from pydantic import ValidationError

import cordia_agent.models.intent as _intent_module
import cordia_agent.state as _state_module
from cordia_agent.tools.contracts import (
    CatalogItem,
    CatalogSearchResult,
    CustomerContextView,
    OperationStatusView,
    PolicyValidationResult,
    TenantContextView,
    VerificationGrantView,
)
from cordia_agent.tools.status_codes import (
    ValidateBookingDraftStatus,
    VerificationAction,
)


# ---------------------------------------------------------------------------
# §6.1 PolicyValidationResult — spec fixture round-trip
# ---------------------------------------------------------------------------

_POLICY_FIXTURE = {
    "valid": False,
    "code": "minimum_notice_not_met",
    "customer_message": "Bookings must be made at least 24 hours in advance.",
    "recoverable_fields": ["starts_at"],
    "suggested_action": "request_alternative_time",
    "policy_version": "policy_2026_06_15",
}


@pytest.mark.unit
def test_policy_validation_result_matches_spec_fixture() -> None:
    """Build from the exact §6.1 fixture and assert all six fields round-trip."""
    result = PolicyValidationResult.model_validate(_POLICY_FIXTURE)

    assert result.valid is False
    assert result.code == ValidateBookingDraftStatus.MINIMUM_NOTICE_NOT_MET
    assert (
        result.customer_message == "Bookings must be made at least 24 hours in advance."
    )
    assert result.recoverable_fields == ["starts_at"]
    assert result.suggested_action == "request_alternative_time"
    assert result.policy_version == "policy_2026_06_15"


@pytest.mark.unit
def test_policy_validation_result_rejects_extra_fields() -> None:
    """extra='forbid' must reject unknown keys."""
    with pytest.raises(ValidationError):
        PolicyValidationResult.model_validate(
            {**_POLICY_FIXTURE, "unexpected_key": "x"}
        )


@pytest.mark.unit
def test_policy_validation_result_valid_path() -> None:
    """A 'valid' result with code=valid round-trips correctly."""
    result = PolicyValidationResult.model_validate(
        {
            "valid": True,
            "code": "valid",
            "customer_message": "All checks passed.",
            "policy_version": "policy_2026_06_15",
        }
    )
    assert result.valid is True
    assert result.code == ValidateBookingDraftStatus.VALID
    assert result.recoverable_fields == []
    assert result.suggested_action is None


# ---------------------------------------------------------------------------
# Inv-16 OperationStatusView — normalized projection only
# ---------------------------------------------------------------------------

_OP_STATUS_BASE = {
    "operation_id": "op-abc123",
    "status": "booked",
    "customer_message": "Your booking is confirmed.",
    "pending": False,
    "terminal": True,
    "updated_at": "2026-06-15T10:30:00+10:00",
}


@pytest.mark.unit
def test_operation_status_view_valid_status_round_trip() -> None:
    """A normalized status value ('booked') validates and round-trips."""
    view = OperationStatusView.model_validate(_OP_STATUS_BASE)
    assert view.operation_id == "op-abc123"
    assert view.status.value == "booked"
    assert view.pending is False
    assert view.terminal is True


@pytest.mark.unit
@pytest.mark.parametrize(
    "internal_substate",
    [
        "validating",
        "held",
        "awaiting_payment",
        "dispatching",
        "unknown_internal",
    ],
)
def test_operation_status_view_rejects_internal_substates(
    internal_substate: str,
) -> None:
    """Internal substates must be REJECTED by NormalizedOperationStatus typing (Inv-16).

    This proves the type only admits the 9-value public projection and never
    exposes internal pipeline states to the agent/customer boundary.
    """
    with pytest.raises(ValidationError):
        OperationStatusView.model_validate(
            {**_OP_STATUS_BASE, "status": internal_substate}
        )


@pytest.mark.unit
def test_operation_status_view_full_round_trip() -> None:
    """Full view with all optional fields round-trips correctly."""
    from cordia_agent.contracts.operation_status import NormalizedOperationStatus
    from cordia_agent.tools.status_codes import AvailabilityMode

    view = OperationStatusView.model_validate(
        {
            "operation_id": "op-xyz789",
            "status": "in_progress",
            "booking_id": "bk-001",
            "booking_reference": "CORD-20260615-001",
            "availability_mode": "live",
            "customer_message": "Processing your booking.",
            "pending": True,
            "terminal": False,
            "updated_at": "2026-06-15T09:00:00+00:00",
        }
    )
    assert view.status == NormalizedOperationStatus.IN_PROGRESS
    assert view.booking_id == "bk-001"
    assert view.booking_reference == "CORD-20260615-001"
    assert view.availability_mode == AvailabilityMode.LIVE


@pytest.mark.unit
def test_operation_status_view_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        OperationStatusView.model_validate({**_OP_STATUS_BASE, "surprise": "field"})


# ---------------------------------------------------------------------------
# VerificationGrantView
# ---------------------------------------------------------------------------

_GRANT_BASE = {
    "tenant_id": "tenant-001",
    "allowed_action": "read_sensitive",
    "expires_at": "2026-06-15T12:00:00+00:00",
    "scope": "session",
}


@pytest.mark.unit
@pytest.mark.parametrize("action", list(VerificationAction))
def test_verification_grant_view_accepts_all_actions(
    action: VerificationAction,
) -> None:
    """allowed_action accepts each VerificationAction value."""
    view = VerificationGrantView.model_validate(
        {**_GRANT_BASE, "allowed_action": action.value}
    )
    assert view.allowed_action == action


@pytest.mark.unit
def test_verification_grant_view_rejects_unknown_action() -> None:
    """An unrecognised action string must be rejected."""
    with pytest.raises(ValidationError):
        VerificationGrantView.model_validate(
            {**_GRANT_BASE, "allowed_action": "teleport"}
        )


@pytest.mark.unit
@pytest.mark.parametrize("scope", ["session", "operation"])
def test_verification_grant_view_valid_scopes(scope: str) -> None:
    """scope accepts only 'session' or 'operation'."""
    view = VerificationGrantView.model_validate({**_GRANT_BASE, "scope": scope})
    assert view.scope == scope


@pytest.mark.unit
def test_verification_grant_view_rejects_invalid_scope() -> None:
    """A scope value outside {'session', 'operation'} must be rejected."""
    with pytest.raises(ValidationError):
        VerificationGrantView.model_validate({**_GRANT_BASE, "scope": "global"})


@pytest.mark.unit
def test_verification_grant_view_optional_fields_default() -> None:
    view = VerificationGrantView.model_validate(_GRANT_BASE)
    assert view.grant_id is None
    assert view.customer_id is None
    assert view.target_booking_id is None


@pytest.mark.unit
def test_verification_grant_view_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        VerificationGrantView.model_validate({**_GRANT_BASE, "extra": "bad"})


# ---------------------------------------------------------------------------
# VerificationAction value set
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_verification_action_value_set() -> None:
    """VerificationAction must have exactly the 6 specified values."""
    assert {m.value for m in VerificationAction} == {
        "read_sensitive",
        "modify",
        "cancel",
        "use_payment_method",
        "refund",
        "owner_override",
    }


# ---------------------------------------------------------------------------
# No duplicate model declarations — the schema exporter depends on it
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_contracts_booking_draft_is_canonical() -> None:
    """The BookingDraft the tool contracts use IS cordia_agent.state.BookingDraft.

    Not merely structurally equal — the SAME object. A second class with the
    same title reaches the schema exporter as a duplicate ``$defs`` entry and
    ``validate_no_duplicates`` fails the build, so this pins the one property
    that keeps Python, C# and TypeScript describing one model rather than two.
    """
    from cordia_agent.tools import contracts

    assert contracts.BookingDraft is _state_module.BookingDraft


@pytest.mark.unit
def test_contracts_confirmation_and_revision_are_canonical() -> None:
    """Same guarantee for the two spec-02 intent models."""
    from cordia_agent.tools import contracts

    assert contracts.Confirmation is _intent_module.Confirmation
    assert contracts.IntentRevision is _intent_module.IntentRevision


# ---------------------------------------------------------------------------
# Optional: TenantContextView, CustomerContextView, CatalogItem build + reject-extra
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_tenant_context_view_round_trip() -> None:
    view = TenantContextView.model_validate(
        {
            "tenant_id": "t-001",
            "policy_version": "policy_2026_06_15",
            "timezone": "Australia/Sydney",
            "supported_operations": ["create", "cancel"],
            "capabilities": ["voice", "sms"],
            "currency": "AUD",
        }
    )
    assert view.tenant_id == "t-001"
    assert view.timezone == "Australia/Sydney"
    assert view.currency == "AUD"


@pytest.mark.unit
def test_tenant_context_view_rejects_extra() -> None:
    with pytest.raises(ValidationError):
        TenantContextView.model_validate(
            {
                "tenant_id": "t-001",
                "policy_version": "p1",
                "timezone": "UTC",
                "supported_operations": [],
                "capabilities": [],
                "currency": "USD",
                "surprise": "x",
            }
        )


@pytest.mark.unit
def test_customer_context_view_defaults() -> None:
    """CustomerContextView builds with all optional fields absent."""
    view = CustomerContextView.model_validate({})
    assert view.customer_id is None
    assert view.display_name is None
    assert view.usual_party_size is None
    assert view.communication_preference is None
    assert view.has_tokenized_payment is False


@pytest.mark.unit
def test_customer_context_view_rejects_extra() -> None:
    with pytest.raises(ValidationError):
        CustomerContextView.model_validate({"nope": "x"})


@pytest.mark.unit
def test_catalog_item_round_trip() -> None:
    item = CatalogItem.model_validate(
        {
            "item_id": "item-001",
            "name": "Massage 60 min",
            "price_minor": 9000,
            "currency": "AUD",
            "catalog_version": "v2",
        }
    )
    assert item.item_id == "item-001"
    assert item.price_minor == 9000
    assert item.description is None
    assert item.duration_minutes is None


@pytest.mark.unit
def test_catalog_search_result_empty_default() -> None:
    result = CatalogSearchResult.model_validate({})
    assert result.items == []


@pytest.mark.unit
def test_catalog_item_rejects_extra() -> None:
    with pytest.raises(ValidationError):
        CatalogItem.model_validate(
            {
                "item_id": "x",
                "name": "x",
                "price_minor": 100,
                "currency": "USD",
                "catalog_version": "v1",
                "unknown_field": "bad",
            }
        )
