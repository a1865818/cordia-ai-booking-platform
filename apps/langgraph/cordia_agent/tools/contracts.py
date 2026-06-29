"""Tool-contract envelope and pure-read payload models for the agent<->platform boundary.

Spec refs: 03 §4.2 (envelope), §4.3 (pure-read Out schemas), §6.1 (PolicyValidationResult),
§4.3 bounded side-effect + durable mutation I/O models, §4.4 manage-booking reads.

These Pydantic models are the source of truth for the shared types exported to
C# and TypeScript/Zod via ``tools/scripts/export_pydantic_schemas.py``.
"""

from __future__ import annotations

from typing import Literal

from pydantic import AwareDatetime, BaseModel, Field

from cordia_agent.contracts.operation_status import NormalizedOperationStatus
from cordia_agent.tools._external_stubs import BookingDraft, Confirmation
from cordia_agent.tools.status_codes import (
    AvailabilityMode,
    RequestHumanHandoffStatus,
    ValidateBookingChangeStatus,
    ValidateBookingDraftStatus,
    VerificationAction,
)


SCHEMA_VERSION = "1.0.0"


class ToolResultEnvelope(BaseModel):
    """Single closed wrapper returned by every tool (spec 03 §4.2)."""

    model_config = {"extra": "forbid"}

    ok: bool = Field(description="Machine-readable success/failure.")
    status: str = Field(description="Per-tool status code; never raw error text.")
    customer_hint: str | None = Field(
        default=None,
        description="Safe customer-facing string; never raw provider text.",
    )
    recoverable_fields: list[str] = Field(
        default_factory=list, description="Fields the agent may re-prompt to correct."
    )
    suggested_action: str | None = Field(
        default=None,
        description="Machine hint for next move, e.g. request_alternative_time.",
    )
    data: dict | None = Field(
        default=None, description="Tool-specific typed payload (per-tool Out schemas)."
    )
    correlation_id: str = Field(
        description="Propagated from the typed tool client; consumed by observability."
    )
    schema_version: str = Field(
        default=SCHEMA_VERSION, description="Cross-language contract version."
    )


# ---------------------------------------------------------------------------
# Pure-read foreground tool output models (spec 03 §4.3)
# These ride inside ToolResultEnvelope.data.
# ---------------------------------------------------------------------------


class TenantContextView(BaseModel):
    """Output payload for get_tenant_context (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    tenant_id: str = Field(description="Stable tenant identifier.")
    policy_version: str = Field(description="Active tenant policy version.")
    timezone: str = Field(
        description="IANA timezone for the tenant, e.g. 'Australia/Sydney'."
    )
    supported_operations: list[str] = Field(
        description="Operation types supported by this tenant, e.g. ['create', 'cancel']."
    )
    capabilities: list[str] = Field(
        description="Feature-flag capability names active for this tenant."
    )
    currency: str = Field(description="ISO 4217 currency code, e.g. 'AUD'.")


class CustomerContextView(BaseModel):
    """Output payload for find_customer_context (spec 03 §4.3).

    Named CustomerContextView (not CustomerContext) to avoid clashing with
    cordia_agent.state.CustomerContext, which has a different shape.
    Minimized: no sensitive history is included here (§9.2).
    """

    model_config = {"extra": "forbid"}

    customer_id: str | None = Field(
        default=None, description="Stable customer ID where known."
    )
    display_name: str | None = Field(
        default=None, description="Customer display name if available."
    )
    usual_party_size: int | None = Field(
        default=None, description="Customer's typical party size preference."
    )
    communication_preference: str | None = Field(
        default=None, description="Preferred communication channel, e.g. 'sms'."
    )
    has_tokenized_payment: bool = Field(
        default=False, description="Whether the customer has a stored payment token."
    )


class CatalogItem(BaseModel):
    """A single catalog item returned by search_vendor_catalog / get_catalog_item (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    item_id: str = Field(description="Stable catalog item identifier.")
    name: str = Field(description="Human-readable item name.")
    description: str | None = Field(
        default=None, description="Optional item description."
    )
    price_minor: int = Field(
        description="Price in the currency's minor unit (e.g. cents for AUD)."
    )
    currency: str = Field(description="ISO 4217 currency code for price_minor.")
    catalog_version: str = Field(description="Catalog version this item was read from.")


class CatalogSearchResult(BaseModel):
    """Output payload for search_vendor_catalog (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    items: list[CatalogItem] = Field(
        default_factory=list, description="Matching catalog items; empty if none found."
    )


class PolicyValidationResult(BaseModel):
    """Output payload for validate_booking_draft (spec 03 §6.1).

    All six fields must round-trip from the spec §6.1 JSON fixture exactly.
    """

    model_config = {"extra": "forbid"}

    valid: bool = Field(description="True iff the draft passes all policy checks.")
    code: ValidateBookingDraftStatus = Field(
        description="Structured policy/validation outcome code."
    )
    customer_message: str = Field(
        description="Safe customer-facing explanation of the validation outcome."
    )
    recoverable_fields: list[str] = Field(
        default_factory=list,
        description="Draft fields the agent may re-prompt to correct.",
    )
    suggested_action: str | None = Field(
        default=None,
        description="Machine hint for the agent's next move, e.g. request_alternative_time.",
    )
    policy_version: str = Field(
        description="Tenant policy version the validation was run against."
    )


class VerificationGrantView(BaseModel):
    """Output payload for check_verification (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    grant_id: str | None = Field(
        default=None, description="Opaque grant identifier; None if not yet issued."
    )
    tenant_id: str = Field(description="Tenant scope for this grant.")
    customer_id: str | None = Field(
        default=None, description="Customer the grant belongs to, if identified."
    )
    target_booking_id: str | None = Field(
        default=None, description="Booking this grant targets, if operation-scoped."
    )
    allowed_action: VerificationAction = Field(
        description="The single action this grant permits."
    )
    expires_at: AwareDatetime = Field(
        description="Grant expiry time (offset-aware ISO-8601)."
    )
    scope: Literal["session", "operation"] = Field(
        description="'session' for broad session scope; 'operation' for a single operation."
    )


class OperationStatusView(BaseModel):
    """Output payload for get_operation_status (spec 03 §4.3 / spec 06 D2).

    ``status`` is typed as NormalizedOperationStatus (9-value public projection,
    Inv-16). Internal substates (e.g. 'validating', 'held', 'awaiting_payment')
    are never surfaced here; Pydantic will reject them at validation time.
    """

    model_config = {"extra": "forbid"}

    operation_id: str = Field(description="Stable operation identifier.")
    status: NormalizedOperationStatus = Field(
        description=(
            "Normalized 9-value operation status (Inv-16). "
            "Internal substates are NEVER valid here."
        )
    )
    booking_id: str | None = Field(
        default=None, description="Booking ID once confirmed, if available."
    )
    booking_reference: str | None = Field(
        default=None, description="Human-readable booking reference, if available."
    )
    availability_mode: AvailabilityMode | None = Field(
        default=None, description="Availability mode at last status update."
    )
    customer_message: str = Field(
        description="Safe customer-facing status description."
    )
    pending: bool = Field(
        description="True iff the operation is still in a non-terminal state."
    )
    terminal: bool = Field(
        description="True iff the operation has reached a terminal state."
    )
    updated_at: AwareDatetime = Field(
        description="Last status update time (offset-aware ISO-8601)."
    )


# ---------------------------------------------------------------------------
# Bounded side-effect tool models (spec 03 §4.3)
# ---------------------------------------------------------------------------


class AvailabilityRequest(BaseModel):
    """Input payload for check_candidate_availability (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    draft: BookingDraft = Field(description="Booking draft to check availability for.")
    want_hold: bool = Field(
        default=False,
        description="Whether to request a hold on the candidate slot.",
    )
    hold_ttl_seconds: int | None = Field(
        default=None,
        description="How long (seconds) to hold the slot; None uses tenant default.",
    )
    hold_idempotency_key: str | None = Field(
        default=None,
        description="Idempotency key for the hold request; None if want_hold is False.",
    )


class AvailabilityCandidate(BaseModel):
    """A single available candidate slot (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    starts_at: AwareDatetime = Field(
        description="Candidate slot start time (offset-aware ISO-8601)."
    )
    duration_minutes: int = Field(
        description="Duration of the candidate slot in minutes."
    )
    availability_mode: AvailabilityMode = Field(
        description="Availability confidence mode for this candidate."
    )


class HoldResult(BaseModel):
    """Hold confirmation present only inside a 'held' availability response (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    hold_id: str = Field(description="Opaque hold identifier assigned by the platform.")
    expires_at: AwareDatetime = Field(
        description="When the hold expires (offset-aware ISO-8601)."
    )


class AvailabilityResult(BaseModel):
    """Output payload for check_candidate_availability (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    mode: AvailabilityMode = Field(
        description="Overall availability mode for the response."
    )
    candidates: list[AvailabilityCandidate] = Field(
        default_factory=list,
        description="Available candidate slots; empty when unavailable.",
    )
    hold: HoldResult | None = Field(
        default=None,
        description="Hold details; only present when mode=held.",
    )
    invalid_fields: list[str] = Field(
        default_factory=list,
        description="Draft fields that prevented availability lookup.",
    )
    alternatives: list[AvailabilityCandidate] = Field(
        default_factory=list,
        description="Alternative slots suggested when the requested time is unavailable.",
    )


class VerificationRequest(BaseModel):
    """Input payload for request_verification (spec 03 §4.3). No card/payment fields (Inv-12)."""

    model_config = {"extra": "forbid"}

    target_booking_id: str | None = Field(
        default=None,
        description="Booking the verification is scoped to, if operation-scoped.",
    )
    customer_id: str | None = Field(
        default=None,
        description="Customer to send verification to, if identified.",
    )
    channel: Literal["sms", "email"] = Field(
        description="Channel to send the verification on."
    )
    action: VerificationAction = Field(
        description="The action this verification request will authorize."
    )
    verification_idempotency_key: str = Field(
        description="Idempotency key for this verification send request."
    )


class VerificationSentResult(BaseModel):
    """Output payload for request_verification (spec 03 §4.3). No card/payment fields (Inv-12)."""

    model_config = {"extra": "forbid"}

    verification_id: str = Field(
        description="Opaque verification identifier assigned by the platform."
    )
    channel: Literal["sms", "email"] = Field(
        description="Channel the verification was sent on."
    )
    expires_at: AwareDatetime = Field(
        description="When the verification code expires (offset-aware ISO-8601)."
    )
    masked_destination: str = Field(
        description="Masked destination address, e.g. '+61 4** *** 321' or 'j***@example.com'."
    )


class HandoffRequest(BaseModel):
    """Input payload for request_human_handoff (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    session_id: str = Field(description="Current dialog session identifier.")
    reason_code: str = Field(
        description="Structured reason code for the handoff, e.g. 'complaint'."
    )
    handoff_idempotency_key: str = Field(
        description="Idempotency key preventing duplicate work-item creation."
    )
    recommended_action: str | None = Field(
        default=None,
        description="Optional machine hint for the human agent's first action.",
    )


class HandoffResult(BaseModel):
    """Output payload for request_human_handoff (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    work_item_id: str = Field(
        description="Stable work-item identifier in the handoff queue."
    )
    queue: str = Field(description="Queue the work item was routed to.")
    status: RequestHumanHandoffStatus = Field(
        description="Outcome status: created, existing, or routing_unavailable."
    )


# ---------------------------------------------------------------------------
# Durable mutation tool models (spec 03 §4.3 / §4.4)
# ---------------------------------------------------------------------------


class SubmitConfirmedIntentInput(BaseModel):
    """Input for submit_confirmed_intent — ALL fields REQUIRED, NO defaults (Inv-2, spec 03 §4.3 / §10.4).

    operation_id, idempotency_key, and intent_payload_hash are opaque strings minted by spec 04.
    """

    model_config = {"extra": "forbid"}

    operation_id: str = Field(
        description="Stable operation identifier minted by spec 04; no default (Inv-2)."
    )
    idempotency_key: str = Field(
        description="Request idempotency key; no default (Inv-2)."
    )
    intent_id: str = Field(description="Stable intent identifier across revisions.")
    intent_revision: int = Field(
        description="Monotonic revision number being submitted."
    )
    intent_payload_hash: str = Field(
        description="Canonical payload hash; must equal the bound revision's hash (Inv-2)."
    )
    confirmation: Confirmation = Field(
        description="Immutable confirmation evidence bound to this revision (§8.8)."
    )


class AcceptanceResult(BaseModel):
    """Output payload for submit_confirmed_intent — §10.4 acceptance shape."""

    model_config = {"extra": "forbid"}

    operation_id: str = Field(description="Stable operation identifier.")
    intent_id: str = Field(description="Stable intent identifier.")
    intent_revision: int = Field(description="Revision number that was accepted.")
    status: str = Field(
        description="Acceptance status string, e.g. 'accepted' (§10.4)."
    )
    status_url: str = Field(
        description="URL to poll for operation status, e.g. '/api/booking-operations/{operation_id}'."
    )
    submitted_at: AwareDatetime = Field(
        description="When the intent was accepted by the platform (offset-aware ISO-8601)."
    )


class SubmitModificationInput(BaseModel):
    """Input for submit_booking_modification — ALL identity fields REQUIRED, NO defaults (Inv-2, Inv-11).

    expected_booking_version and grant_id enforce optimistic concurrency and authorization (Inv-11).
    """

    model_config = {"extra": "forbid"}

    target_booking_id: str = Field(
        description="Booking to modify; no default (Inv-11)."
    )
    expected_booking_version: int = Field(
        description="Optimistic concurrency version; must match current booking version (Inv-11)."
    )
    operation_id: str = Field(
        description="Stable operation identifier; no default (Inv-2)."
    )
    modification_idempotency_key: str = Field(
        description="Idempotency key for this modification request; no default (Inv-2)."
    )
    intent_revision: int = Field(
        description="Monotonic revision number being submitted."
    )
    intent_payload_hash: str = Field(
        description="Canonical payload hash for the modification intent (Inv-2)."
    )
    confirmation: Confirmation = Field(
        description="Immutable confirmation evidence bound to this revision (§8.8)."
    )
    grant_id: str = Field(
        description="Authorization grant for this modification operation (Inv-11)."
    )


class SubmitCancellationInput(BaseModel):
    """Input for submit_booking_cancellation — ALL identity fields REQUIRED, NO defaults (Inv-2, Inv-11).

    expected_booking_version and grant_id enforce optimistic concurrency and authorization (Inv-11).
    confirmation is optional per §4.4 (some cancellations do not require explicit confirmation).
    """

    model_config = {"extra": "forbid"}

    target_booking_id: str = Field(
        description="Booking to cancel; no default (Inv-11)."
    )
    expected_booking_version: int = Field(
        description="Optimistic concurrency version; must match current booking version (Inv-11)."
    )
    operation_id: str = Field(
        description="Stable operation identifier; no default (Inv-2)."
    )
    cancellation_idempotency_key: str = Field(
        description="Idempotency key for this cancellation request; no default (Inv-2)."
    )
    reason_code: str = Field(
        description="Structured reason code for the cancellation, e.g. 'customer_request'."
    )
    grant_id: str = Field(
        description="Authorization grant for this cancellation operation (Inv-11)."
    )
    confirmation: Confirmation | None = Field(
        default=None,
        description="Optional confirmation evidence; required only when policy demands it (§4.4).",
    )


# ---------------------------------------------------------------------------
# Manage-booking read models (spec 03 §4.4)
# ---------------------------------------------------------------------------


class ManageableBooking(BaseModel):
    """Minimized non-sensitive disambiguator for a booking manageable by the customer (§9.4.1)."""

    model_config = {"extra": "forbid"}

    booking_id: str = Field(description="Stable booking identifier.")
    booking_reference: str = Field(
        description="Human-readable booking reference, e.g. 'CORD-20260615-001'."
    )
    business: str = Field(description="Business name for this booking.")
    date: str = Field(
        description="Booking date as a display string, e.g. '2026-06-15'."
    )
    approx_time: str = Field(
        description="Approximate booking time as a display string, e.g. '10:30 AM'."
    )
    party_size: int = Field(description="Number of people in the booking.")


class BookingProjectionRef(BaseModel):
    """Minimal verification-leveled booking projection placeholder (§4.4 / §9.4).

    Fuller projection to be added in a later spec track.
    """

    model_config = {"extra": "forbid"}

    booking_id: str = Field(description="Stable booking identifier.")
    booking_reference: str | None = Field(
        default=None,
        description="Human-readable booking reference if available.",
    )
    status: str | None = Field(
        default=None,
        description="Booking status string if available.",
    )
    starts_at: AwareDatetime | None = Field(
        default=None,
        description="Booking start time (offset-aware ISO-8601) if available.",
    )
    party_size: int | None = Field(
        default=None,
        description="Number of people in the booking if available.",
    )


class BookingChangeValidation(BaseModel):
    """Output payload for validate_booking_change (spec 03 §9.4.3)."""

    model_config = {"extra": "forbid"}

    valid: bool = Field(
        description="True iff the proposed change passes all policy checks."
    )
    code: ValidateBookingChangeStatus = Field(
        description="Structured outcome code for the change validation."
    )
    fees_minor: int | None = Field(
        default=None,
        description="Change fee in the currency's minor unit (e.g. cents), if applicable.",
    )
    currency: str | None = Field(
        default=None,
        description="ISO 4217 currency code for fees_minor, if applicable.",
    )
    requires_confirmation: bool = Field(
        default=False,
        description="Whether the agent must obtain explicit customer confirmation before proceeding.",
    )
    requires_payment: bool = Field(
        default=False,
        description="Whether additional payment is required to complete the change.",
    )
    recoverable_fields: list[str] = Field(
        default_factory=list,
        description="Fields the agent may re-prompt to correct.",
    )
    policy_version: str = Field(
        description="Tenant policy version the validation was run against."
    )
    capacity_mode: AvailabilityMode = Field(
        description="Availability mode at the time the change validation was run."
    )
