"""Submission contract carriers for the agent→API booking-operation flow (spec 04 §4.2).

These models cross the Python→C# boundary via the type-sharing pipeline.
They are the wire-level request/response types for the submission/acceptance flow
and must NOT duplicate the lean LLM tool-input models in tools/contracts.py.

Spec refs: 04 §4.2 (SubmitConfirmedIntentRequest), §6.1/§10.4 (OperationAcceptedResponse),
§5.1/Inv-3 (IdempotencyConflict).
"""

from __future__ import annotations

from pydantic import AwareDatetime, BaseModel, Field

from cordia_agent.models.intent import Confirmation, IntentRevision


class SubmitConfirmedIntentRequest(BaseModel):
    """Agent→API POST body for submitting a confirmed booking intent (spec 04 §4.2).

    operation_id and idempotency_key are minted and checkpointed exactly once (Inv-2).
    replaces_operation_id is set only when this submission supersedes a prior operation
    (§10.1/§13.5).
    """

    model_config = {"extra": "forbid"}

    operation_id: str = Field(
        description="Stable operation identifier minted and checkpointed once (Inv-2)."
    )
    idempotency_key: str = Field(
        description="Request idempotency key minted and checkpointed once (Inv-2)."
    )
    intent_id: str = Field(description="Stable intent identifier across revisions.")
    intent_revision: int = Field(
        description="Monotonic revision number being submitted."
    )
    replaces_operation_id: str | None = Field(
        default=None,
        description="Operation ID superseded by this submission (§10.1/§13.5); None if no prior operation.",
    )
    revision: IntentRevision = Field(
        description="Canonical, hashed intent revision body (spec 02 §5.2/§8.7)."
    )
    confirmation: Confirmation = Field(
        description="Immutable confirmation evidence bound to this revision (spec 02 §5.2/§8.8)."
    )


class OperationAcceptedResponse(BaseModel):
    """202 response body returned when the API durably accepts a submission (spec 04 §6.1/§10.4).

    'accepted' means the operation is durably recorded — NOT booked (Inv-16).
    """

    model_config = {"extra": "forbid"}

    operation_id: str = Field(description="Stable operation identifier.")
    intent_id: str = Field(description="Stable intent identifier.")
    intent_revision: int = Field(description="Revision number that was accepted.")
    status: str = Field(
        description="Acceptance status string, e.g. 'accepted' — durably accepted, NOT booked (Inv-16)."
    )
    status_url: str = Field(
        description="Stable pollable URL for operation status, e.g. '/api/booking-operations/{operation_id}'."
    )
    accepted_at: AwareDatetime = Field(
        description="Timestamp when acceptance was committed (offset-aware ISO-8601)."
    )


class IdempotencyConflict(BaseModel):
    """409 response body when the idempotency key is already bound to a prior operation (spec 04 §5.1, Inv-3)."""

    model_config = {"extra": "forbid"}

    idempotency_key: str = Field(
        description="The idempotency key that is already bound."
    )
    existing_operation_id: str = Field(
        description="The operation already bound to this idempotency key."
    )
    message: str = Field(
        description="Agent/customer-safe explanation of the conflict; no raw internal detail."
    )
