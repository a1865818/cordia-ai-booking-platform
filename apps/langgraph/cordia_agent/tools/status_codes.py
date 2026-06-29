"""Per-tool status enums and the AvailabilityMode binding (spec 03 §4.3/§4.4).

These ``(str, Enum)`` types are the source of truth for the shared status codes
exported to C# and TypeScript/Zod via ``tools/scripts/export_pydantic_schemas.py``.
"""

from __future__ import annotations

from enum import Enum


class GetTenantContextStatus(str, Enum):
    """Status codes for get_tenant_context (spec 03 §4.3)."""

    RESOLVED = "resolved"
    TENANT_NOT_RESOLVED = "tenant_not_resolved"


class FindCustomerContextStatus(str, Enum):
    """Status codes for find_customer_context (spec 03 §4.3)."""

    FOUND = "found"
    MULTIPLE = "multiple"
    NONE = "none"


class CatalogItemStatus(str, Enum):
    """Status codes shared by search_vendor_catalog and get_catalog_item (spec 03 §4.3)."""

    OK = "ok"
    NOT_FOUND = "not_found"
    CATALOG_UNAVAILABLE = "catalog_unavailable"


class ValidateBookingDraftStatus(str, Enum):
    """Status codes for validate_booking_draft (spec 03 §4.3)."""

    VALID = "valid"
    MINIMUM_NOTICE_NOT_MET = "minimum_notice_not_met"
    OUTSIDE_OPERATING_HOURS = "outside_operating_hours"
    PARTY_SIZE_UNSUPPORTED = "party_size_unsupported"
    SERVICE_INELIGIBLE = "service_ineligible"
    FIELD_MISSING = "field_missing"
    FIELD_AMBIGUOUS = "field_ambiguous"


class CheckCandidateAvailabilityStatus(str, Enum):
    """Status codes for check_candidate_availability (spec 03 §4.3)."""

    AVAILABLE = "available"
    AVAILABLE_PENDING = "available_pending"
    UNAVAILABLE = "unavailable"
    HELD = "held"
    HOLD_FAILED = "hold_failed"


class RequestVerificationStatus(str, Enum):
    """Status codes for request_verification (spec 03 §4.3)."""

    SENT = "sent"
    ALREADY_SENT = "already_sent"
    RATE_LIMITED = "rate_limited"
    NO_VERIFIED_DESTINATION = "no_verified_destination"


class CheckVerificationStatus(str, Enum):
    """Status codes for check_verification (spec 03 §4.3)."""

    GRANTED = "granted"
    PENDING = "pending"
    FAILED = "failed"
    EXPIRED = "expired"


class SubmitConfirmedIntentStatus(str, Enum):
    """Status codes for submit_confirmed_intent (spec 03 §4.3)."""

    ACCEPTED = "accepted"
    DUPLICATE_ACCEPTED = "duplicate_accepted"
    IDEMPOTENCY_CONFLICT = "idempotency_conflict"
    CONFIRMATION_INVALID = "confirmation_invalid"
    POLICY_FAILED = "policy_failed"
    VALIDATION_FAILED = "validation_failed"


class RequestHumanHandoffStatus(str, Enum):
    """Status codes for request_human_handoff (spec 03 §4.3)."""

    CREATED = "created"
    EXISTING = "existing"
    ROUTING_UNAVAILABLE = "routing_unavailable"


class FindManageableBookingsStatus(str, Enum):
    """Status codes for find_manageable_bookings (spec 03 §4.4)."""

    FOUND = "found"
    MULTIPLE = "multiple"
    NONE = "none"


class GetBookingDetailsStatus(str, Enum):
    """Status codes for get_booking_details (spec 03 §4.4)."""

    OK = "ok"
    REQUIRES_VERIFICATION = "requires_verification"
    NOT_FOUND = "not_found"


class ValidateBookingChangeStatus(str, Enum):
    """Status codes for validate_booking_change (spec 03 §4.4)."""

    VALID = "valid"
    POLICY_FAILED = "policy_failed"
    STALE_VERSION = "stale_version"
    CAPACITY_UNAVAILABLE = "capacity_unavailable"


class SubmitBookingModificationStatus(str, Enum):
    """Status codes for submit_booking_modification (spec 03 §4.4)."""

    ACCEPTED = "accepted"
    DUPLICATE_ACCEPTED = "duplicate_accepted"
    IDEMPOTENCY_CONFLICT = "idempotency_conflict"
    STALE_VERSION = "stale_version"
    VERIFICATION_REQUIRED = "verification_required"
    CONFIRMATION_INVALID = "confirmation_invalid"


class SubmitBookingCancellationStatus(str, Enum):
    """Status codes for submit_booking_cancellation (spec 03 §4.4)."""

    ACCEPTED = "accepted"
    ALREADY_CANCELLED = "already_cancelled"
    DUPLICATE_ACCEPTED = "duplicate_accepted"
    IDEMPOTENCY_CONFLICT = "idempotency_conflict"
    STALE_VERSION = "stale_version"
    VERIFICATION_REQUIRED = "verification_required"


# AvailabilityMode is OWNED by spec 07 (07-capacity-and-holds.md). It is bound
# here as a frozen value set so the tool contracts can type against it now.
# DO NOT redefine elsewhere: spec 07 must IMPORT this symbol, not re-declare it
# (export_pydantic_schemas.validate_no_duplicates fails on a duplicate title).
class AvailabilityMode(str, Enum):
    """Availability mode bounding agent wording (owned by spec 07)."""

    INFORMATIONAL = "informational"
    LIVE = "live"
    HELD = "held"
    CONFIRMED = "confirmed"


# VerificationAction is the cross-service set OWNED by spec 08. It is bound here
# as a frozen value set so tool contracts can type allowed_action now.
# DO NOT redefine elsewhere: spec 08 must IMPORT this symbol, not re-declare it
# (export_pydantic_schemas.validate_no_duplicates fails on a duplicate title).
class VerificationAction(str, Enum):
    """Allowed action types for a verification grant (owned by spec 08)."""

    READ_SENSITIVE = "read_sensitive"
    MODIFY = "modify"
    CANCEL = "cancel"
    USE_PAYMENT_METHOD = "use_payment_method"
    REFUND = "refund"
    OWNER_OVERRIDE = "owner_override"
