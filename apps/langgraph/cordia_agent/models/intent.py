"""Intent revision and confirmation models (spec 02 §5.2, §8.7-8.8)."""

from __future__ import annotations

from typing import Literal

from pydantic import AwareDatetime, BaseModel, Field

from cordia_agent.state import BookingRequest


class IntentRevision(BaseModel):
    """Immutable submitted booking revision (spec 02 §5.2 / §8.7)."""

    model_config = {"extra": "forbid"}

    intent_id: str = Field(description="Stable intent identity across revisions.")
    revision: int = Field(description="Monotonic revision number for this intent.")
    canonical_payload: BookingRequest = Field(
        description="Normalized request, no provenance."
    )
    schema_version: str = Field(
        description="Draft schema version carried into the intent."
    )
    tenant_policy_version: str = Field(
        description="Tenant configuration/policy version."
    )
    payload_hash: str = Field(
        description="Canonical payload hash; algorithm fixed by §10.2."
    )
    created_at: AwareDatetime = Field(
        description="Revision creation time (offset-aware)."
    )
    source_session_id: str = Field(
        description="Dialog session that produced this revision."
    )
    prior_revision: int | None = Field(
        default=None, description="Superseded revision number."
    )
    supersession_reason: str | None = Field(
        default=None, description="Why the prior revision was superseded (§8.7)."
    )


class Confirmation(BaseModel):
    """Immutable confirmation evidence bound to exactly one IntentRevision (spec 02 §5.2 / §8.8)."""

    model_config = {"extra": "forbid"}

    confirmation_id: str = Field(description="Stable confirmation identity.")
    intent_id: str = Field(description="Intent this confirmation is bound to.")
    intent_revision: int = Field(description="Revision number this confirmation binds.")
    intent_payload_hash: str = Field(
        description="MUST equal the bound revision's payload_hash (§8.8)."
    )
    identity: str = Field(
        description="Verified identity, or 'anonymous_session' sentinel."
    )
    confirmation_channel: Literal["voice", "sms", "web", "email"] = Field(
        description="Channel confirmation was given on."
    )
    confirmed_at: AwareDatetime = Field(description="Confirmation time (offset-aware).")
    source_turn_id: str = Field(
        description="transcript.final turn that carried the confirmation (Inv-1)."
    )
    read_back_version: str = Field(
        description="Exact deterministic read-back version confirmed (§8.6)."
    )
    disclosures_presented: list[str] = Field(
        default_factory=list,
        description="Consent + policy disclosures shown (§5.5/§14).",
    )
    verification_level: str = Field(
        description="Verification level recorded at confirmation (§9.3)."
    )


def confirmation_binds_revision(
    confirmation: Confirmation, revision: IntentRevision
) -> bool:
    """§8.8 submit guard: a Confirmation is valid only when it binds exactly this revision.

    Submission MUST fail if confirmation.intent_payload_hash != revision.payload_hash.
    """
    return (
        confirmation.intent_id == revision.intent_id
        and confirmation.intent_revision == revision.revision
        and confirmation.intent_payload_hash == revision.payload_hash
    )
