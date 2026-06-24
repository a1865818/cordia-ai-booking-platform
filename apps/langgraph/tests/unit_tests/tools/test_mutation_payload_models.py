"""Unit tests for mutation + bounded-side-effect tool I/O models (spec 03 §4.3 / §4.4).

Covers: SubmitConfirmedIntentInput (Inv-2), AcceptanceResult (§10.4), SubmitModificationInput
(Inv-11), SubmitCancellationInput (Inv-11), VerificationRequest / VerificationSentResult,
HandoffRequest / HandoffResult, AvailabilityRequest / AvailabilityCandidate / HoldResult /
AvailabilityResult, ManageableBooking / BookingProjectionRef / BookingChangeValidation, and
Inv-12 (no card/payment-instrument field names on any submit/verification model).
"""

from __future__ import annotations

import pytest
from pydantic import ValidationError

from cordia_agent.tools.contracts import (
    AcceptanceResult,
    AvailabilityRequest,
    AvailabilityResult,
    BookingChangeValidation,
    BookingProjectionRef,
    HandoffRequest,
    HandoffResult,
    HoldResult,
    ManageableBooking,
    SubmitCancellationInput,
    SubmitConfirmedIntentInput,
    SubmitModificationInput,
    VerificationRequest,
    VerificationSentResult,
)
from cordia_agent.tools.status_codes import (
    AvailabilityMode,
    RequestHumanHandoffStatus,
    ValidateBookingChangeStatus,
    VerificationAction,
)


# ---------------------------------------------------------------------------
# Helpers — minimal valid fixture builders
# ---------------------------------------------------------------------------


def _confirmation_dict() -> dict:
    return {
        "confirmation_id": "conf-001",
        "intent_id": "intent-001",
        "intent_revision": 1,
        "intent_payload_hash": "hash-abc",
        "identity": "anonymous_session",
        "confirmation_channel": "voice",
        "confirmed_at": "2026-06-15T10:30:00Z",
        "source_turn_id": "turn-001",
        "read_back_version": "v1",
        "verification_level": "session",
    }


def _booking_draft_dict() -> dict:
    return {
        "tenant_id": "t-001",
        "session_id": "sess-001",
        "schema_version": "1.0.0",
        "tenant_policy_version": "policy_2026_06_15",
        "request": {"operation": "create"},
        "source": {"channel": "voice", "language": "en"},
    }


def _valid_submit_confirmed_intent() -> dict:
    return {
        "operation_id": "op_01JX123",
        "idempotency_key": "idem-001",
        "intent_id": "intent_01ABC",
        "intent_revision": 3,
        "intent_payload_hash": "sha256-xyz",
        "confirmation": _confirmation_dict(),
    }


def _valid_submit_modification() -> dict:
    return {
        "target_booking_id": "bk-001",
        "expected_booking_version": 2,
        "operation_id": "op_mod_001",
        "modification_idempotency_key": "mod-idem-001",
        "intent_revision": 1,
        "intent_payload_hash": "sha256-mod",
        "confirmation": _confirmation_dict(),
        "grant_id": "grant-001",
    }


def _valid_submit_cancellation() -> dict:
    return {
        "target_booking_id": "bk-001",
        "expected_booking_version": 2,
        "operation_id": "op_cancel_001",
        "cancellation_idempotency_key": "cancel-idem-001",
        "reason_code": "customer_request",
        "grant_id": "grant-001",
    }


# ---------------------------------------------------------------------------
# Inv-2 — SubmitConfirmedIntentInput: all 6 fields REQUIRED, NO defaults
# ---------------------------------------------------------------------------

_SUBMIT_INTENT_REQUIRED_FIELDS = [
    "operation_id",
    "idempotency_key",
    "intent_id",
    "intent_revision",
    "intent_payload_hash",
    "confirmation",
]


@pytest.mark.unit
def test_submit_confirmed_intent_empty_raises() -> None:
    """SubmitConfirmedIntentInput() with no fields must raise ValidationError (Inv-2)."""
    with pytest.raises(ValidationError):
        SubmitConfirmedIntentInput.model_validate({})


@pytest.mark.unit
@pytest.mark.parametrize("omitted_field", _SUBMIT_INTENT_REQUIRED_FIELDS)
def test_submit_confirmed_intent_missing_field_raises(omitted_field: str) -> None:
    """Omitting any single required field must raise ValidationError (Inv-2)."""
    data = {
        k: v for k, v in _valid_submit_confirmed_intent().items() if k != omitted_field
    }
    with pytest.raises(ValidationError):
        SubmitConfirmedIntentInput.model_validate(data)


@pytest.mark.unit
def test_submit_confirmed_intent_happy_path() -> None:
    """A fully populated SubmitConfirmedIntentInput round-trips correctly."""
    obj = SubmitConfirmedIntentInput.model_validate(_valid_submit_confirmed_intent())
    assert obj.operation_id == "op_01JX123"
    assert obj.idempotency_key == "idem-001"
    assert obj.intent_id == "intent_01ABC"
    assert obj.intent_revision == 3
    assert obj.intent_payload_hash == "sha256-xyz"
    assert obj.confirmation.confirmation_id == "conf-001"


@pytest.mark.unit
def test_submit_confirmed_intent_rejects_extra() -> None:
    """extra='forbid' must reject unknown keys."""
    with pytest.raises(ValidationError):
        SubmitConfirmedIntentInput.model_validate(
            {**_valid_submit_confirmed_intent(), "surprise": "x"}
        )


# ---------------------------------------------------------------------------
# §10.4 AcceptanceResult — exact spec fixture round-trip
# ---------------------------------------------------------------------------

_ACCEPTANCE_FIXTURE = {
    "operation_id": "op_01JX123ABC",
    "intent_id": "intent_01DEFGHI",
    "intent_revision": 3,
    "status": "accepted",
    "status_url": "/api/booking-operations/op_01JX123ABC",
    "submitted_at": "2026-06-15T10:30:00Z",
}


@pytest.mark.unit
def test_acceptance_result_spec_fixture_round_trip() -> None:
    """AcceptanceResult built from §10.4 fixture must have all six fields correct."""
    result = AcceptanceResult.model_validate(_ACCEPTANCE_FIXTURE)
    assert result.operation_id == "op_01JX123ABC"
    assert result.intent_id == "intent_01DEFGHI"
    assert result.intent_revision == 3
    assert result.status == "accepted"
    assert result.status_url == "/api/booking-operations/op_01JX123ABC"
    assert result.submitted_at.isoformat().startswith("2026-06-15")


@pytest.mark.unit
def test_acceptance_result_rejects_extra() -> None:
    with pytest.raises(ValidationError):
        AcceptanceResult.model_validate({**_ACCEPTANCE_FIXTURE, "extra_field": "x"})


# ---------------------------------------------------------------------------
# Inv-11 — SubmitModificationInput / SubmitCancellationInput: grant_id +
#           expected_booking_version are REQUIRED, NO defaults
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_submit_modification_missing_expected_version_raises() -> None:
    """expected_booking_version is required on SubmitModificationInput (Inv-11)."""
    data = {
        k: v
        for k, v in _valid_submit_modification().items()
        if k != "expected_booking_version"
    }
    with pytest.raises(ValidationError):
        SubmitModificationInput.model_validate(data)


@pytest.mark.unit
def test_submit_modification_missing_grant_id_raises() -> None:
    """grant_id is required on SubmitModificationInput (Inv-11)."""
    data = {k: v for k, v in _valid_submit_modification().items() if k != "grant_id"}
    with pytest.raises(ValidationError):
        SubmitModificationInput.model_validate(data)


@pytest.mark.unit
def test_submit_modification_happy_path() -> None:
    obj = SubmitModificationInput.model_validate(_valid_submit_modification())
    assert obj.target_booking_id == "bk-001"
    assert obj.expected_booking_version == 2
    assert obj.grant_id == "grant-001"
    assert obj.modification_idempotency_key == "mod-idem-001"


@pytest.mark.unit
def test_submit_cancellation_missing_expected_version_raises() -> None:
    """expected_booking_version is required on SubmitCancellationInput (Inv-11)."""
    data = {
        k: v
        for k, v in _valid_submit_cancellation().items()
        if k != "expected_booking_version"
    }
    with pytest.raises(ValidationError):
        SubmitCancellationInput.model_validate(data)


@pytest.mark.unit
def test_submit_cancellation_missing_grant_id_raises() -> None:
    """grant_id is required on SubmitCancellationInput (Inv-11)."""
    data = {k: v for k, v in _valid_submit_cancellation().items() if k != "grant_id"}
    with pytest.raises(ValidationError):
        SubmitCancellationInput.model_validate(data)


@pytest.mark.unit
def test_submit_cancellation_happy_path() -> None:
    obj = SubmitCancellationInput.model_validate(_valid_submit_cancellation())
    assert obj.target_booking_id == "bk-001"
    assert obj.expected_booking_version == 2
    assert obj.grant_id == "grant-001"
    assert obj.confirmation is None  # optional per §4.4


@pytest.mark.unit
def test_submit_cancellation_with_confirmation() -> None:
    data = {**_valid_submit_cancellation(), "confirmation": _confirmation_dict()}
    obj = SubmitCancellationInput.model_validate(data)
    assert obj.confirmation is not None
    assert obj.confirmation.confirmation_id == "conf-001"


# ---------------------------------------------------------------------------
# Inv-12 — NO card/payment-instrument field names on submit_* / verification models
# ---------------------------------------------------------------------------

_FORBIDDEN_SUBSTRINGS = {
    "pan",
    "cvv",
    "cvc",
    "card",
    "ccv",
    "card_number",
    "cardnumber",
    "security_code",
}

_SUBMIT_AND_VERIFICATION_MODELS = [
    SubmitConfirmedIntentInput,
    SubmitModificationInput,
    SubmitCancellationInput,
    VerificationRequest,
    VerificationSentResult,
]


@pytest.mark.unit
@pytest.mark.parametrize(
    "model_cls", _SUBMIT_AND_VERIFICATION_MODELS, ids=lambda c: c.__name__
)
def test_no_card_payment_fields(model_cls: type) -> None:
    """No field name on submit_* or verification models may contain a payment-instrument substring (Inv-12).

    Checks case-insensitive substring match against the forbidden set.
    """
    forbidden_substrings = {s.lower() for s in _FORBIDDEN_SUBSTRINGS}
    violations: list[str] = [
        f"{model_cls.__name__}.{field_name} contains '{forbidden}'"
        for field_name in model_cls.model_fields
        for forbidden in forbidden_substrings
        if forbidden in field_name.lower()
    ]
    assert not violations, (
        f"Payment-instrument field names found (Inv-12): {violations}"
    )


# ---------------------------------------------------------------------------
# AvailabilityRequest / AvailabilityCandidate / HoldResult / AvailabilityResult
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_availability_request_defaults() -> None:
    """want_hold defaults to False; optional hold fields default to None."""
    draft = _booking_draft_dict()
    obj = AvailabilityRequest.model_validate({"draft": draft})
    assert obj.want_hold is False
    assert obj.hold_ttl_seconds is None
    assert obj.hold_idempotency_key is None


@pytest.mark.unit
def test_availability_request_with_hold() -> None:
    draft = _booking_draft_dict()
    obj = AvailabilityRequest.model_validate(
        {
            "draft": draft,
            "want_hold": True,
            "hold_ttl_seconds": 300,
            "hold_idempotency_key": "hold-idem-001",
        }
    )
    assert obj.want_hold is True
    assert obj.hold_ttl_seconds == 300
    assert obj.hold_idempotency_key == "hold-idem-001"


@pytest.mark.unit
def test_hold_result_requires_both_fields() -> None:
    """HoldResult requires hold_id and expires_at (no defaults)."""
    with pytest.raises(ValidationError):
        HoldResult.model_validate({"hold_id": "h-001"})
    with pytest.raises(ValidationError):
        HoldResult.model_validate({"expires_at": "2026-06-15T12:00:00Z"})


@pytest.mark.unit
def test_hold_result_happy_path() -> None:
    obj = HoldResult.model_validate(
        {"hold_id": "h-001", "expires_at": "2026-06-15T12:00:00Z"}
    )
    assert obj.hold_id == "h-001"


@pytest.mark.unit
def test_availability_result_hold_defaults_none() -> None:
    """AvailabilityResult.hold is None by default."""
    obj = AvailabilityResult.model_validate({"mode": "live"})
    assert obj.hold is None
    assert obj.candidates == []
    assert obj.invalid_fields == []
    assert obj.alternatives == []


@pytest.mark.unit
def test_availability_result_with_hold() -> None:
    obj = AvailabilityResult.model_validate(
        {
            "mode": "held",
            "candidates": [
                {
                    "starts_at": "2026-06-15T10:30:00+10:00",
                    "duration_minutes": 60,
                    "availability_mode": "held",
                }
            ],
            "hold": {"hold_id": "h-002", "expires_at": "2026-06-15T11:00:00+10:00"},
        }
    )
    assert obj.mode == AvailabilityMode.HELD
    assert len(obj.candidates) == 1
    assert obj.hold is not None
    assert obj.hold.hold_id == "h-002"


@pytest.mark.unit
def test_availability_result_rejects_extra() -> None:
    with pytest.raises(ValidationError):
        AvailabilityResult.model_validate({"mode": "live", "unknown": "x"})


# ---------------------------------------------------------------------------
# VerificationRequest / VerificationSentResult
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_verification_request_happy_path() -> None:
    obj = VerificationRequest.model_validate(
        {
            "channel": "sms",
            "action": "modify",
            "verification_idempotency_key": "vrf-idem-001",
        }
    )
    assert obj.channel == "sms"
    assert obj.action == VerificationAction.MODIFY
    assert obj.target_booking_id is None
    assert obj.customer_id is None


@pytest.mark.unit
def test_verification_request_missing_required_raises() -> None:
    """channel, action, and verification_idempotency_key are required."""
    with pytest.raises(ValidationError):
        VerificationRequest.model_validate({"channel": "sms", "action": "modify"})


@pytest.mark.unit
def test_verification_request_rejects_extra() -> None:
    with pytest.raises(ValidationError):
        VerificationRequest.model_validate(
            {
                "channel": "sms",
                "action": "modify",
                "verification_idempotency_key": "k",
                "pan": "4111111111111111",
            }
        )


@pytest.mark.unit
def test_verification_sent_result_round_trip() -> None:
    obj = VerificationSentResult.model_validate(
        {
            "verification_id": "vrf-001",
            "channel": "email",
            "expires_at": "2026-06-15T11:00:00Z",
            "masked_destination": "j***@example.com",
        }
    )
    assert obj.verification_id == "vrf-001"
    assert obj.channel == "email"
    assert obj.masked_destination == "j***@example.com"


@pytest.mark.unit
def test_verification_sent_result_rejects_extra() -> None:
    with pytest.raises(ValidationError):
        VerificationSentResult.model_validate(
            {
                "verification_id": "vrf-001",
                "channel": "email",
                "expires_at": "2026-06-15T11:00:00Z",
                "masked_destination": "j***@example.com",
                "cvv": "123",
            }
        )


# ---------------------------------------------------------------------------
# HandoffRequest / HandoffResult
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_handoff_request_happy_path() -> None:
    obj = HandoffRequest.model_validate(
        {
            "session_id": "sess-001",
            "reason_code": "complaint",
            "handoff_idempotency_key": "ho-idem-001",
        }
    )
    assert obj.session_id == "sess-001"
    assert obj.recommended_action is None


@pytest.mark.unit
def test_handoff_request_with_recommended_action() -> None:
    obj = HandoffRequest.model_validate(
        {
            "session_id": "sess-001",
            "reason_code": "complaint",
            "handoff_idempotency_key": "ho-idem-001",
            "recommended_action": "escalate_to_supervisor",
        }
    )
    assert obj.recommended_action == "escalate_to_supervisor"


@pytest.mark.unit
def test_handoff_result_round_trip() -> None:
    obj = HandoffResult.model_validate(
        {
            "work_item_id": "wi-001",
            "queue": "general",
            "status": "created",
        }
    )
    assert obj.work_item_id == "wi-001"
    assert obj.status == RequestHumanHandoffStatus.CREATED


@pytest.mark.unit
def test_handoff_result_rejects_extra() -> None:
    with pytest.raises(ValidationError):
        HandoffResult.model_validate(
            {
                "work_item_id": "wi-001",
                "queue": "general",
                "status": "created",
                "extra": "x",
            }
        )


# ---------------------------------------------------------------------------
# ManageableBooking / BookingProjectionRef / BookingChangeValidation
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_manageable_booking_round_trip() -> None:
    obj = ManageableBooking.model_validate(
        {
            "booking_id": "bk-001",
            "booking_reference": "CORD-20260615-001",
            "business": "Serenity Spa",
            "date": "2026-06-15",
            "approx_time": "10:30 AM",
            "party_size": 2,
        }
    )
    assert obj.booking_id == "bk-001"
    assert obj.business == "Serenity Spa"
    assert obj.party_size == 2


@pytest.mark.unit
def test_manageable_booking_rejects_extra() -> None:
    with pytest.raises(ValidationError):
        ManageableBooking.model_validate(
            {
                "booking_id": "bk-001",
                "booking_reference": "CORD-20260615-001",
                "business": "Serenity Spa",
                "date": "2026-06-15",
                "approx_time": "10:30 AM",
                "party_size": 2,
                "extra_field": "x",
            }
        )


@pytest.mark.unit
def test_booking_projection_ref_defaults() -> None:
    """BookingProjectionRef only requires booking_id; all others default to None."""
    obj = BookingProjectionRef.model_validate({"booking_id": "bk-001"})
    assert obj.booking_id == "bk-001"
    assert obj.booking_reference is None
    assert obj.status is None
    assert obj.starts_at is None
    assert obj.party_size is None


@pytest.mark.unit
def test_booking_change_validation_round_trip() -> None:
    obj = BookingChangeValidation.model_validate(
        {
            "valid": True,
            "code": "valid",
            "policy_version": "policy_2026_06_15",
            "capacity_mode": "live",
        }
    )
    assert obj.valid is True
    assert obj.code == ValidateBookingChangeStatus.VALID
    assert obj.fees_minor is None
    assert obj.requires_confirmation is False
    assert obj.requires_payment is False
    assert obj.recoverable_fields == []


@pytest.mark.unit
def test_booking_change_validation_with_fees() -> None:
    obj = BookingChangeValidation.model_validate(
        {
            "valid": True,
            "code": "valid",
            "fees_minor": 500,
            "currency": "AUD",
            "requires_confirmation": True,
            "requires_payment": True,
            "policy_version": "policy_2026_06_15",
            "capacity_mode": "live",
        }
    )
    assert obj.fees_minor == 500
    assert obj.currency == "AUD"
    assert obj.requires_confirmation is True
    assert obj.requires_payment is True


@pytest.mark.unit
def test_booking_change_validation_rejects_extra() -> None:
    with pytest.raises(ValidationError):
        BookingChangeValidation.model_validate(
            {
                "valid": True,
                "code": "valid",
                "policy_version": "p1",
                "capacity_mode": "live",
                "unexpected": "x",
            }
        )
