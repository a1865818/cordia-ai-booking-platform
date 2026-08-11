"""Core state models for the Cordia agent.

These Pydantic models are the source of truth for the shared types exported to
C# and TypeScript via ``tools/scripts/export_pydantic_schemas.py``.
"""

from __future__ import annotations

from enum import Enum
from typing import Literal

from pydantic import AwareDatetime, BaseModel, Field, PositiveInt, model_validator

from cordia_agent.contracts.preorder import PreorderDraft, PreorderReference
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
    contact_ref: str | None = Field(
        default=None,
        description=(
            "Opaque handle to contact details held out-of-band in `guest_contacts`. "
            "Preferred over the inline fields on the canonical payload: the payload is hashed "
            "and immutable, so anything inline can never be erased without breaking the "
            "idempotency chain (Inv-2/3). A ref is erasable by deleting one row."
        ),
    )


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
    # Pre-order (spec 11 §4.3): the immutable, revalidated-before-commit reference embedded in
    # the canonical payload so a pre-order is part of the canonical request hash (Inv-1/Inv-2 —
    # changing any line forces a new revision). None ⇒ no pre-order; the canonical hasher omits
    # null/empty so plain bookings hash identically to before this field existed.
    preorder: PreorderReference | None = Field(
        default=None,
        description="Immutable pre-order reference, or None when no pre-order.",
    )
    # Guest identity (spec 04 §4.2): the ratified `customer_ref` / `notification_destination`
    # slot on the canonical, hashed body, carried as the existing CustomerRef so both lanes use
    # one shape (BookingDraft.customer already does). Without it a booking has no notification
    # destination and `GET /api/bookings?phone_e164=` cannot find it.
    #
    # In the hash by design — spec 04 puts `notification_destination` in `IntentRevisionPayload`.
    # Inv-12's scope is PAN/CVV/government-id/secrets, which this never carries. canon.v1 omits
    # null/{} so a payload without a guest hashes exactly as it did before this field existed.
    #
    # NOTE the retention asymmetry: booking_intent_revisions is immutable and replayed, and the
    # retention sweep only anonymises ConversationSession.customer_ref — so whatever lands here
    # is permanent. Send the minimum that identifies the guest; the console never sends
    # display_name.
    customer: CustomerRef | None = Field(
        default=None,
        description="Guest reference (spec 04 §4.2); None when the guest is unknown.",
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
    # Pre-order (spec 11 §4.3): the MUTABLE in-dialog draft the agent assembles from the published
    # catalog. Frozen into request.preorder (immutable PreorderReference) at confirmation via
    # PreorderDraft.to_reference(...). Foreground state only — stripped from the canonical projection
    # (like field_provenance) so it is never itself a booking side effect (§2: the agent only drafts).
    preorder_draft: PreorderDraft | None = Field(
        default=None,
        description="Mutable in-dialog pre-order draft, or None when none in progress.",
    )
    source: SourceInfo = Field(description="Origin channel + language.")
    field_provenance: dict[str, FieldProvenance] = Field(
        default_factory=dict,
        description="Internal capture metadata keyed by field; stripped on submit.",
    )

    def to_canonical(self) -> CanonicalBookingDraft:  # DQ2=B
        """Project to the canonical payload, dropping internal-only fields.

        Strips ``field_provenance`` and the foreground-only ``preorder_draft`` (the immutable
        ``request.preorder`` reference is what is canonical); the TriState distinction is already
        inline on each request field (DQ1=A), so absent/unknown/declined survive.
        """
        return CanonicalBookingDraft.model_validate(
            self.model_dump(exclude={"field_provenance", "preorder_draft"})
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
    allowed_agent_policy_ids: list[str] = Field(default_factory=list)
    allowed_tool_policy_ids: list[str] = Field(default_factory=list)
    # Session bootstrap fields, mapped from AgentInputState so downstream nodes
    # (e.g. identify_intent) can construct the initial BookingDraft.
    source: SourceInfo | None = Field(
        default=None, description="Origin channel + language (from AgentInputState)."
    )
    customer: CustomerRef | None = Field(
        default=None,
        description="Customer reference from the channel gateway, if known.",
    )
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
    # --- Consent-gate channels (spec 14 §5.1/§5.2) ---
    # consent_state/recording_allowed are exported concepts (persisted via the
    # Booking API); pending_consent_notice is an internal channel consumed by
    # the consent_gate → END await loop and the voice pipeline notice playback.
    consent_state: dict | None = Field(
        default=None,
        description=(
            "Recorded ConsentState (spec 14 §4.1) as a plain dict once the "
            "outcome has been durably persisted via record_consent; None until "
            "then. Stored as dict to keep AgentState channels JSON-merge-safe."
        ),
    )
    recording_allowed: bool | None = Field(
        default=None,
        description=(
            "Transcript/audio persistence gate (Inv-12, §5.2). None = consent "
            "gate has not resolved yet; the checkpointer suppresses the "
            "transcript whenever this is False."
        ),
    )
    pending_consent_notice: str | None = Field(
        default=None,
        description=(
            "Internal: notice text awaiting a customer consent response. "
            "Non-None means the gate emitted the notice and is awaiting the "
            "customer's granted/declined reply (§5.2 AwaitOutcome)."
        ),
    )
    consent_policy: dict | None = Field(
        default=None,
        description=(
            "Internal: the ConsentPolicyView resolved on the gate's first "
            "pass, stashed so the reply turn records against the SAME policy "
            "version the customer was shown (no re-resolution)."
        ),
    )
    consent_reply: str | None = Field(
        default=None,
        description=(
            "Internal: a classified consent reply ('granted'/'declined') "
            "whose record_consent persist failed — retried next turn without "
            "re-asking the customer."
        ),
    )
    # --- Internal clarification channels (02-G, §8.5) ---
    # These fields are NOT exported to AgentInputState/AgentOutputState and do
    # not appear in the generated JSON schemas.  They are purely internal
    # LangGraph channels consumed by the clarify → capture_fields loop.
    clarification_attempts: dict[str, int] = Field(
        default_factory=dict,
        description=(
            "Internal: per-field clarification attempt counts, "
            "keyed by BookingRequest field name (§8.5)."
        ),
    )
    pending_clarify_action: str | None = Field(
        default=None,
        description=(
            "Internal: ClarifyAction value decided by the clarify node (§8.5). "
            "ESCALATE_HANDOFF is surfaced here; it does NOT add a graph edge."
        ),
    )
    pending_clarify_prompt: str | None = Field(
        default=None,
        description="Internal: the clarifying-question prompt for the current turn (§8.5).",
    )
    pending_clarify_field: str | None = Field(
        default=None,
        description="Internal: BookingRequest field name targeted by the active clarification (§8.5).",
    )
    # --- Availability tracking (02-H, §8.7) ---
    # Stored as plain types to avoid a circular import:
    # state.py MUST NOT import tools.contracts / tools.foreground.
    # availability_mode is already imported from tools.status_codes (leaf module — OK).
    availability_candidates: list[dict] = Field(
        default_factory=list,
        description=(
            "Internal: serialised AvailabilityCandidate dicts from the last "
            "check_candidate_availability call (§8.7). Stored as list[dict] to "
            "avoid a circular import with tools.contracts."
        ),
    )
    availability_invalid_fields: list[str] = Field(
        default_factory=list,
        description=(
            "Internal: invalid draft fields returned by the availability tool (§8.7). "
            "Non-empty → router sends back to capture_fields."
        ),
    )
    availability_pending: bool = Field(
        default=False,
        description=(
            "Internal: True when the availability response has status 'available_pending' "
            "(§6.6, Inv-16). Advisory only — NEVER treated as held/confirmed/success."
        ),
    )
    availability_checked: bool = Field(
        default=False,
        description=(
            "Internal: True once check_candidates has run for the current draft. "
            "Distinguishes 'the slot is genuinely full' (checked, zero candidates) "
            "from 'we have not looked yet' (the session's default), which is what "
            "lets clarify offer another time instead of re-asking for a field the "
            "customer already gave."
        ),
    )
    # --- Read-back + confirmation channels (02-I, §8.6/§8.8/§8.9) ---
    # Stored as plain types (str/bool/dict/list) to avoid a circular import:
    # state.py MUST NOT import IntentRevision/Confirmation (models.intent imports
    # BookingRequest from state → cycle).  Nodes build the typed models locally
    # and persist .model_dump(mode="json") here.
    read_back_text: str | None = Field(
        default=None,
        description="Internal: rendered read-back text for the current draft (§8.6).",
    )
    read_back_version: str | None = Field(
        default=None,
        description=(
            "Internal: deterministic sha256 content hash of the read-back render "
            "(§8.6); used by await_confirmation to bind the Confirmation."
        ),
    )
    read_back_first_presented_turn_id: str | None = Field(
        default=None,
        description=(
            "Internal: the turn on which the CURRENT read_back_version was first "
            "rendered (§8.9). A confirmation spoken on that same turn cannot be a "
            "response to it — the customer had not heard it yet — so "
            "await_confirmation refuses one. Re-rendering an unchanged read-back "
            "on a later turn leaves this alone."
        ),
    )
    read_back_interrupted: bool = Field(
        default=False,
        description=(
            "Internal: True when the read-back was interrupted/cancelled before "
            "the customer heard the full text (§8.9).  NEVER confirmed when True."
        ),
    )
    disclosures_presented: list[str] = Field(
        default_factory=list,
        description=(
            "Internal: consent + policy disclosure IDs shown to the customer "
            "(§5.5 / §14).  Empty → consent gate refuses to record a Confirmation."
        ),
    )
    verification_level: str | None = Field(
        default=None,
        description="Internal: verification level recorded at confirmation time (§9.3).",
    )
    current_intent_id: str | None = Field(
        default=None,
        description="Internal: stable intent identity across revisions (§8.7).",
    )
    current_revision: dict | None = Field(
        default=None,
        description=(
            "Internal: IntentRevision.model_dump(mode='json') for the current revision "
            "(§8.7 / Inv-10).  Stored as dict to avoid circular import with models.intent."
        ),
    )
    confirmation: dict | None = Field(
        default=None,
        description=(
            "Internal: Confirmation.model_dump(mode='json') recorded by await_confirmation "
            "(§8.8).  None until the customer gives explicit confirmation (Inv-1)."
        ),
    )
    # --- Verification + handoff channels (02-K, §8.6/§8.11) ---
    # Stored as plain types (str/dict) to avoid circular imports.
    verification_id: str | None = None
    verification_outcome: str | None = None
    handoff_reason_code: str | None = None
    handoff_package: dict | None = None
    handoff_result: dict | None = None
    # --- Submission outcome channels (02-J, §8.9/§10.1) ---
    # Stored as plain types (str/dict) to avoid circular imports.
    submission_status: str | None = Field(
        default=None,
        description=(
            "Internal: machine-readable submission outcome "
            "('accepted', 'duplicate_accepted', 'validation_failed', "
            "'confirmation_invalid', 'policy_failed', 'idempotency_conflict', "
            "'checkpoint_failed', 'nonrecoverable', 'unavailable'). "
            "Set by submit_intent (02-J)."
        ),
    )
    submission_message: str | None = Field(
        default=None,
        description=(
            "Internal: safe customer-facing wording for the submission outcome "
            "(Inv-16).  Set by submit_intent on acceptance (02-J)."
        ),
    )
    operation_status: dict | None = Field(
        default=None,
        description=(
            "Internal: OperationStatusView.model_dump(mode='json') recovered via "
            "get_operation_status on a reconnect (02-J / Inv-5).  "
            "Stored as dict to avoid circular import with tools.contracts."
        ),
    )


class AgentInputState(BaseModel):
    """Boundary input contract for the foreground agent (extra fields rejected).

    ``signed_tenant_context`` is the ONLY source of tenant identity (Inv-13).
    A plain ``tenant_id`` field is intentionally absent — the tenant is resolved
    exclusively from the verified signed token by the resolve_tenant node.

    The transcript fields carry ONE dialog turn.  They mirror the identically
    named ``AgentState`` channels, so LangGraph maps them straight through and
    the per-turn nodes (``identify_intent``, ``capture_fields``,
    ``await_confirmation``, ``consent_gate``) see the utterance without any
    node-side plumbing.  An ``AgentPort`` implementation (spec 01 §agent_port)
    maps a ``TranscriptFinalFrame`` onto them: ``text`` →
    ``transcript_text``, ``turn_id`` → ``current_turn_id``, and
    ``transcript_is_final=True`` because only committed finals reach the port.
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
    transcript_text: str | None = Field(
        default=None,
        description=(
            "Transcript text for this turn.  Optional so a session can be "
            "opened (consent notice, greeting) before the customer speaks."
        ),
    )
    transcript_is_final: bool = Field(
        default=False,
        description=(
            "True iff the transcript is a committed final (not partial) STT "
            "result.  Defaults to False so Inv-1 fails safe: a caller must "
            "assert finality explicitly before the turn may mutate the draft "
            "or confirm a read-back."
        ),
    )
    current_turn_id: str | None = Field(
        default=None,
        description=(
            "Turn/message ID for this turn; stamped onto captured fields and "
            "the confirmation record for provenance."
        ),
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
