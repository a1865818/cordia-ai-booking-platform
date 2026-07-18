"""Active-RedactionPolicy registry (spec 14 §4.1).

The spec loads the tenant's ``RedactionPolicy`` (configured sensitive-field
selectors + masking mode) from tenant config and applies it agent-side. No
tenant-config service exists yet (spec 15/02 territory), so this module is the
injection seam: the hosting layer calls :func:`set_redaction_policy` when it
resolves tenant configuration; every redaction call site defaults to
:func:`get_redaction_policy`. Until a policy is set, the always-on baseline
(PAN/CVV/gov-id, empty tenant list — the Stage-3 gate default) applies.
"""

from __future__ import annotations

from cordia_agent.contracts.security import RedactionPolicy


_active_policy: RedactionPolicy | None = None


def get_redaction_policy() -> RedactionPolicy:
    """Return the active tenant policy, or the always-on baseline default."""
    return _active_policy if _active_policy is not None else RedactionPolicy()


def set_redaction_policy(policy: RedactionPolicy) -> None:
    """Install the tenant's policy — called by the hosting layer at session start."""
    global _active_policy
    _active_policy = policy


def reset_redaction_policy() -> None:
    """Clear the active policy (test teardown / session end)."""
    global _active_policy
    _active_policy = None
