"""Spec-14 foreground security package: consent gate, redaction, policy registry."""

from cordia_agent.security.consent import consent_gate, route_after_consent_gate
from cordia_agent.security.policy import (
    get_redaction_policy,
    reset_redaction_policy,
    set_redaction_policy,
)
from cordia_agent.security.redaction import redact_payload, redact_text


__all__ = [
    "consent_gate",
    "get_redaction_policy",
    "redact_payload",
    "redact_text",
    "reset_redaction_policy",
    "route_after_consent_gate",
    "set_redaction_policy",
]
