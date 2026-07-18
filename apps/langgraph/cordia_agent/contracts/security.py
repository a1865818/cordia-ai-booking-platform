"""Cross-service security contracts (spec 14 §4.1).

Pydantic is the source of truth here; C#/TS/Zod are generated from these models
via the type pipeline (``shared-schemas:generate-types``).

- ``SessionConsentState`` — recording/transcription consent outcome carried on the
  session and stamped onto the submission envelope (§24.1, workflow 5.1/5.2).
  ``outcome`` is closed to the ratified ``consent_state`` pg-enum values
  (spec 12 owns the enum; the strings here must match it verbatim).
- ``RedactionPolicy`` — always-on PAN/CVV/government-id masking plus
  tenant-configured selectors (Inv-12, §24.2). Applied agent-side (pre-emit)
  and API-side (pre-persist/pre-log).
- ``AuditEventEnvelope`` — the cross-service shape of an audit record. The
  physical ``audit_events`` row is spec 12's ratified DDL; envelope fields with
  no dedicated column (actor_type, reason_code, decision, data_classes,
  causation_id) ride in the sanitized ``metadata`` jsonb (D13).

The verification-grant reference contract is ``VerificationGrantView`` in
``cordia_agent.tools.contracts`` (spec 08 owns the lifecycle) — deliberately
not duplicated here.
"""

from __future__ import annotations

from typing import Literal

from pydantic import AwareDatetime, BaseModel, Field


class SessionConsentState(BaseModel):
    """Recording/transcription consent captured at session start (spec 14 §4.1)."""

    model_config = {"extra": "forbid"}

    consent_policy_version: str = Field(
        description="Version of the notice/policy presented, e.g. 'us-ca-two-party:2026-01'."
    )
    jurisdiction_policy_id: str = Field(
        description="Policy resolved from tenant + caller jurisdiction."
    )
    outcome: Literal["granted", "declined", "not_required", "alternative_offered"] = (
        Field(
            description="Consent outcome; values match the ratified consent_state pg-enum (spec 12)."
        )
    )
    recording_allowed: bool = Field(
        description="Gate for transcript/audio capture; False ⇒ no transcript persisted (§5.2)."
    )
    captured_at: AwareDatetime = Field(
        description="When the outcome was captured (offset-aware ISO-8601)."
    )
    notice_channel: Literal["voice", "web", "messaging", "email"] = Field(
        description="Channel the required notice was played/displayed on."
    )


class RedactionPolicy(BaseModel):
    """Sensitive-data masking policy (spec 14 §4.1, Inv-12).

    PAN/CVV masking is always-on by contract default; ``configured_sensitive_fields``
    starts empty and is tenant-overridable (Stage-3 gate decision, 2026-07-09).
    """

    model_config = {"extra": "forbid"}

    mask_pan: bool = Field(
        default=True, description="Always-on card-number masking (Inv-12)."
    )
    mask_cvv: bool = Field(
        default=True, description="Always-on CVV/CVC masking (Inv-12)."
    )
    mask_government_id: bool = Field(
        default=True,
        description="Government-identifier masking (SSN/TFN-style patterns).",
    )
    configured_sensitive_fields: list[str] = Field(
        default_factory=list,
        description="Tenant-defined field-name selectors masked in payloads/logs; empty ⇒ none.",
    )
    mask_token: str = Field(
        default="[REDACTED]", description="Replacement token for masked spans."
    )


class AuditEventEnvelope(BaseModel):
    """Cross-service audit record emitted by either lane (spec 14 §4.1, D13)."""

    model_config = {"extra": "forbid"}

    audit_event_id: str = Field(description="Stable opaque id for the audit record.")
    tenant_id: str = Field(description="Owning tenant (Inv-13).")
    correlation_id: str = Field(description="Request/session correlation id (§25.1).")
    causation_id: str | None = Field(
        default=None, description="Id of the message/command that caused this event."
    )
    actor_type: Literal["customer", "owner", "operator", "agent", "system"] = Field(
        description="Class of actor that performed the action."
    )
    actor_id: str = Field(
        description="Actor identity, e.g. operator handle or 'system'."
    )
    action: str = Field(
        description="Action performed, e.g. 'read_sensitive_booking', 'operator_replay'."
    )
    target_type: str = Field(
        description="Kind of target, e.g. 'booking', 'transcript'."
    )
    target_id: str = Field(description="Target identifier.")
    reason_code: str | None = Field(
        default=None,
        description="Required for owner/operator actions (§24.5); None for others.",
    )
    decision: Literal["allow", "deny"] = Field(
        description="Whether the action was allowed or denied (both are audited, §3.1)."
    )
    data_classes: list[str] = Field(
        description="Data classes touched, e.g. ['pii', 'booking', 'payment_token']."
    )
    occurred_at: AwareDatetime = Field(
        description="When the audited action occurred (offset-aware ISO-8601)."
    )
