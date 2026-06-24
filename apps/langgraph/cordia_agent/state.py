"""Core state models for the Cordia agent.

These Pydantic models are the source of truth for the shared types exported to
C# and TypeScript via ``tools/scripts/export_pydantic_schemas.py``.
"""

from __future__ import annotations

from enum import Enum
from typing import Literal

from pydantic import AwareDatetime, BaseModel, Field, PositiveInt, model_validator

from cordia_agent.tools.status_codes import AvailabilityMode


class StreamFrameType(str, Enum):
    """Discriminator for streaming voice-pipeline frames (spec 01 §5.1 / §6.3)."""

    AUDIO_STARTED = "audio.started"
    TRANSCRIPT_PARTIAL = "transcript.partial"
    TRANSCRIPT_FINAL = "transcript.final"
    AGENT_TEXT_DELTA = "agent.text.delta"
    AGENT_TEXT_FINAL = "agent.text.final"
    TOOL_PROGRESS = "tool.progress"
    TTS_AUDIO_DELTA = "tts.audio.delta"
    BARGE_IN_DETECTED = "barge_in.detected"
    TURN_CANCELLED = "turn.cancelled"


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


# ---------------------------------------------------------------------------
# Foreground agent state (§8.3)
# ---------------------------------------------------------------------------


class DialogIntent(str, Enum):
    """Customer dialog intent — broader than OperationType (includes inquire).

    ``inquire`` is informational only; it does not correspond to a booking
    operation and leaves ``AgentState.operation`` as ``None``.
    Distinct from ``OperationType``, which has no ``inquire`` variant.
    """

    create = "create"
    reschedule = "reschedule"
    modify = "modify"
    cancel = "cancel"
    inquire = "inquire"


class CustomerContext(BaseModel):
    """Minimized personalization context for the current dialog session.

    This is a local placeholder until the 03 ``find_customer_context`` output
    model lands (ticket 03-B/02-H).  The ``recent_booking_summaries`` field
    carries sensitive history and is populated **only** when
    ``AgentState.verification_grant_id`` is set (§9.2 — no sensitive history
    without a verification grant).
    """

    model_config = {"extra": "forbid"}

    customer_id: str | None = Field(
        default=None, description="Stable customer ID, if known."
    )
    display_name: str | None = Field(
        default=None, description="Customer display name, if known."
    )
    recent_booking_summaries: list[str] = Field(
        default_factory=list,
        description=(
            "Sensitive history; only populated with a verification grant (§9.2)."
        ),
    )


class AgentState(BaseModel):
    """Internal LangGraph channel state — all fields defaulted (no extra="forbid").

    LangGraph merges node return dicts into the channel state; requiring all
    fields to have defaults lets partial updates work without tripping
    required-field validation.
    """

    tenant_id: str | None = None
    tenant_policy_version: str | None = None
    session_id: str | None = None
    draft: BookingDraft | None = None
    operation: OperationType | None = None
    operation_id: str | None = None
    idempotency_key: str | None = None
    last_confirmed_revision: int | None = None
    availability_mode: AvailabilityMode | None = None
    verification_grant_id: str | None = None
    terminal_state: Literal["submitted", "human_handoff"] | None = None
    # Tenant-context guard fields (02-D)
    signed_tenant_context: str | None = None
    tenant_context_verified: bool = False
    correlation_id: str | None = None
    channel_endpoint_id: str | None = None
    allowed_policy_ids: list[str] = Field(default_factory=list)
    # Dialog-front fields (02-F)
    transcript_text: str | None = Field(
        default=None, description="Current turn transcript text."
    )
    transcript_is_final: bool = Field(
        default=False,
        description=(
            "True iff the transcript is a final (not partial) STT result. "
            "ONLY transcript.final frames may mutate the draft (Inv-1)."
        ),
    )
    current_turn_id: str | None = Field(
        default=None, description="Turn/message ID for the current dialog turn."
    )
    dialog_intent: DialogIntent | None = Field(
        default=None, description="Classified dialog intent for the current turn."
    )
    customer_context: CustomerContext | None = Field(
        default=None, description="Minimized customer context (§9.2)."
    )


class AgentInputState(BaseModel):
    """Boundary input contract for the foreground agent (extra fields rejected).

    ``signed_tenant_context`` is the ONLY source of tenant identity (Inv-13).
    A plain ``tenant_id`` field is intentionally absent — the tenant is resolved
    exclusively from the verified signed token by the resolve_tenant node.
    """

    model_config = {"extra": "forbid"}

    signed_tenant_context: str = Field(
        description=(
            "Signed TenantContext token from the channel gateway (§7); "
            "the ONLY source of tenant identity, Inv-13."
        )
    )
    session_id: str = Field(description="Dialog session ID.")
    source: SourceInfo = Field(description="Origin channel + language.")
    customer: CustomerRef | None = Field(
        default=None, description="Customer reference if known."
    )


class AgentOutputState(BaseModel):
    """Boundary output contract for the foreground agent (extra fields rejected)."""

    model_config = {"extra": "forbid"}

    terminal_state: Literal["submitted", "human_handoff"] | None = Field(
        default=None, description="Reached terminal state."
    )
    operation_id: str | None = Field(
        default=None, description="Durably-accepted operation id, if submitted."
    )
