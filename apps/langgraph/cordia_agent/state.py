"""Core state models for the Cordia hello graph.

These Pydantic models are the source of truth for the shared types exported to
C# and TypeScript via ``tools/scripts/export_pydantic_schemas.py``.
"""

from __future__ import annotations

from enum import Enum

from pydantic import AwareDatetime, BaseModel, Field, PositiveInt, model_validator


class GreetingStatus(str, Enum):
    """Lifecycle status of a greeting."""

    PENDING = "pending"
    GREETED = "greeted"


class HelloInputState(BaseModel):
    """Input to the hello graph."""

    model_config = {"extra": "forbid"}

    name: str = Field(description="Name of the person to greet.")


class HelloOutputState(BaseModel):
    """Output of the hello graph."""

    model_config = {"extra": "forbid"}

    message: str = Field(description="The rendered greeting message.")
    status: GreetingStatus = Field(
        default=GreetingStatus.GREETED, description="Resulting greeting status."
    )


class TriState(str, Enum):
    """Why an optional field has no concrete value (carried inline on each capture field)."""

    absent = "absent"  # never provided
    unknown = "unknown"  # asked, customer doesn't know
    declined = "declined"  # explicitly refused


class OperationType(str, Enum):
    """Booking operation requested by the customer."""

    create = "create"
    reschedule = "reschedule"
    modify = "modify"
    cancel = "cancel"


class CaptureMethod(str, Enum):
    """How a field value was captured during the dialog."""

    speech = "speech"
    dtmf = "dtmf"
    tool_selection = "tool_selection"
    channel_verified = "channel_verified"


class FieldValidation(str, Enum):
    """Validation outcome for a captured field."""

    valid = "valid"
    invalid = "invalid"
    needs_clarification = "needs_clarification"


class Channel(str, Enum):
    """Origin channel a draft was captured on."""

    voice = "voice"
    sms = "sms"
    web = "web"
    email = "email"


class BookingRequest(BaseModel):
    """A single booking request captured during a dialog."""

    model_config = {"extra": "forbid"}

    operation: OperationType = Field(description="Booking operation requested.")
    # Customer-volunteered scalar optionals carry value | TriState | None inline (DQ1=A).
    # TriState is listed before str with union_mode="left_to_right": since TriState is a
    # str-subclass enum, smart-mode unions resolve sentinel strings ("unknown"/"declined"/
    # "absent") to the plain str branch, silently losing TriState semantics on deserialization.
    # Left-to-right tries TriState first (matches only the 3 sentinels) then falls back to str.
    service_id: TriState | str | None = Field(
        default=None,
        union_mode="left_to_right",
        description="Stable service ID, a TriState, or absent.",
    )
    location_id: TriState | str | None = Field(
        default=None,
        union_mode="left_to_right",
        description="Stable location ID, a TriState, or absent.",
    )
    starts_at: AwareDatetime | TriState | None = Field(
        default=None, description="Offset-aware start, a TriState, or absent."
    )
    party_size: PositiveInt | TriState | None = Field(
        default=None, description="Party size, a TriState, or absent."
    )
    duration_minutes: PositiveInt | TriState | None = Field(
        default=None, description="Duration minutes, a TriState, or absent."
    )
    special_requests: list[str] = Field(
        default_factory=list, description="Free-form special requests."
    )
    # Target/system fields stay plain - TriState is meaningless here (not customer-volunteered).
    target_booking_id: str | None = Field(
        default=None,
        description="Target booking; required for modify/cancel/reschedule.",
    )
    target_booking_version: int | None = Field(
        default=None, description="Optimistic-concurrency version of the target."
    )

    @model_validator(mode="after")
    def _require_target_for_mutations(self) -> BookingRequest:  # DQ4=B
        mutations = {
            OperationType.modify,
            OperationType.cancel,
            OperationType.reschedule,
        }
        if self.operation in mutations and not isinstance(self.target_booking_id, str):
            raise ValueError(
                f"target_booking_id is required for {self.operation.value}"
            )
        return self


class FieldProvenance(BaseModel):
    """Internal-only capture metadata for one field. Stripped on submit."""

    model_config = {"extra": "forbid"}

    source_turn_id: str | None = Field(
        default=None, description="Turn/message ID the value came from."
    )
    stt_confidence: float | None = Field(
        default=None, description="STT/ASR confidence (internal-only)."
    )
    model_confidence: float | None = Field(
        default=None, description="Model confidence (internal-only)."
    )
    capture_method: CaptureMethod = Field(description="How the value was captured.")
    normalized_value: str | None = Field(
        default=None, description="Normalized form of the captured value."
    )
    validation: FieldValidation | None = Field(
        default=None, description="Validation outcome for the field."
    )


class CustomerRef(BaseModel):
    """Reference to the customer; all optional (anonymous sessions)."""

    model_config = {"extra": "forbid"}

    customer_id: str | None = Field(
        default=None, description="Stable customer ID where known."
    )
    display_name: str | None = Field(default=None, description="Customer display name.")
    phone_e164: str | None = Field(
        default=None, description="E.164 phone; plain str (no pattern)."
    )


class SourceInfo(BaseModel):
    """Channel + language the draft originated from."""

    model_config = {"extra": "forbid"}

    channel: Channel = Field(description="Origin channel.")
    language: str = Field(description="BCP-47 language tag, e.g. 'en-AU'.")


class CanonicalBookingDraft(BaseModel):
    """Submitted payload: BookingDraft minus field_provenance (DQ2=B)."""

    model_config = {"extra": "forbid"}

    tenant_id: str = Field(description="Tenant scope (Inv-13).")
    session_id: str = Field(description="Dialog session ID.")
    schema_version: str = Field(
        description="Draft schema version carried into the intent."
    )
    tenant_policy_version: str = Field(
        description="Tenant configuration/policy version."
    )
    customer: CustomerRef | None = Field(
        default=None, description="Customer reference, if known."
    )
    request: BookingRequest = Field(description="The booking request.")
    source: SourceInfo = Field(description="Origin channel + language.")


class BookingDraft(BaseModel):
    """In-dialog typed booking draft; source of the canonical submitted payload."""

    model_config = {"extra": "forbid"}

    tenant_id: str = Field(description="Tenant scope (Inv-13).")
    session_id: str = Field(description="Dialog session ID.")
    schema_version: str = Field(
        description="Draft schema version carried into the intent."
    )
    tenant_policy_version: str = Field(
        description="Tenant configuration/policy version."
    )
    customer: CustomerRef | None = Field(
        default=None, description="Customer reference, if known."
    )
    request: BookingRequest = Field(description="The booking request.")
    source: SourceInfo = Field(description="Origin channel + language.")
    field_provenance: dict[str, FieldProvenance] = Field(
        default_factory=dict,
        description="Internal capture metadata keyed by field; stripped on submit.",
    )

    def to_canonical(self) -> CanonicalBookingDraft:  # DQ2=B
        """Project to the canonical payload, dropping internal-only fields.

        Strips ``field_provenance``; the TriState distinction is already inline
        on each request field (DQ1=A), so absent/unknown/declined survive.
        """
        return CanonicalBookingDraft.model_validate(
            self.model_dump(exclude={"field_provenance"})
        )
