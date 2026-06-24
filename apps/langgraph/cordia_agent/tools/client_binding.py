"""Node-facing typed wrappers over the 03 foreground tools (02-H §client_binding).

Each wrapper invokes the corresponding tool via ``get_tool_client().call_tool(...)``
(by calling ``tool.ainvoke(...)`` — which is the invocation form verified by the
03-E integration tests) and exposes a companion ``parse_*`` helper that
``model_validate``s the returned ``ToolResultEnvelope.data`` into the typed
contract model.

Design:
- Wrappers are thin — NO business logic, NO branching policy, NO state mutation.
- The 03 tools are imported directly from their module (not via ``tools.__init__``)
  to satisfy the 03-E constraint that ``tools/__init__.py`` stays minimal.
- ``from __future__ import annotations`` is used throughout.
- ``parse_*`` helpers return ``None`` when the envelope is not ``ok`` or when
  ``data`` is missing/invalid, so callers can handle degraded responses gracefully.

Spec refs: 02-H §client_binding, spec 03 §4.3.
"""

from __future__ import annotations

import logging
from typing import TYPE_CHECKING

from cordia_agent.tools.contracts import (
    AvailabilityRequest,
    AvailabilityResult,
    CustomerContextView,
    PolicyValidationResult,
    TenantContextView,
    ToolResultEnvelope,
)
from cordia_agent.tools.foreground import (
    check_candidate_availability,
    find_customer_context,
    get_tenant_context,
    validate_booking_draft,
)


if TYPE_CHECKING:
    from pydantic import BaseModel

    from cordia_agent.tools._external_stubs import BookingDraft


_log = logging.getLogger(__name__)

# Envelope-level keys that appear in the raw API body but are NOT part of any
# typed tool-output model.  These must be stripped before passing ``env.data``
# to ``model_validate`` on any ``extra="forbid"`` model.
_ENVELOPE_KEYS: frozenset[str] = frozenset(
    {
        "status",
        "ok",
        "customer_hint",
        "recoverable_fields",
        "suggested_action",
        "correlation_id",
        "schema_version",
    }
)


def _payload(data: dict, model: type[BaseModel]) -> dict:
    """Strip envelope-only keys from *data* before model_validate against *model*.

    The ``ToolClient`` stores the full response body in ``ToolResultEnvelope.data``.
    That body includes envelope keys like ``"status"`` which typed output models
    (``extra="forbid"``) reject. We strip ONLY the envelope keys that are NOT also
    declared fields of the target model — otherwise we would silently drop genuine
    payload fields that happen to share a name with an envelope key (e.g.
    ``PolicyValidationResult.recoverable_fields`` / ``.suggested_action``).

    Args:
        data: The raw ``ToolResultEnvelope.data`` dict.
        model: The target ``extra="forbid"`` model the payload will validate against.

    Returns:
        A copy of *data* with envelope-only keys (not present on *model*) removed.
    """
    strip = _ENVELOPE_KEYS - set(model.model_fields)
    return {k: v for k, v in data.items() if k not in strip}


# ---------------------------------------------------------------------------
# get_tenant_context wrapper
# ---------------------------------------------------------------------------


async def load_tenant_context() -> ToolResultEnvelope:
    """Invoke the get_tenant_context tool and return the raw envelope.

    Calls ``get_tenant_context.ainvoke({})`` — the form verified by 03-E tests.
    Does NOT set tenant context on the ToolClient; that is done by resolve_tenant.

    Returns:
        The ``ToolResultEnvelope`` from the tool (ok=True on success,
        ok=False on any transport/HTTP error with a safe hint).
    """
    return await get_tenant_context.ainvoke({})


def parse_tenant_context(env: ToolResultEnvelope) -> TenantContextView | None:
    """Parse a ``TenantContextView`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`load_tenant_context`.

    Returns:
        A validated ``TenantContextView`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None`` (degraded — caller logs and continues).
    """
    if not env.ok or env.data is None:
        _log.warning(
            "load_tenant_context returned non-ok or empty data: %s", env.status
        )
        return None
    try:
        return TenantContextView.model_validate(_payload(env.data, TenantContextView))
    except Exception:
        _log.exception("Failed to parse TenantContextView from envelope data")
        return None


# ---------------------------------------------------------------------------
# find_customer_context wrapper
# ---------------------------------------------------------------------------


async def discover_customer_context(
    *,
    phone_e164: str | None = None,
    email: str | None = None,
    caller_id: str | None = None,
    booking_reference: str | None = None,
) -> ToolResultEnvelope:
    """Invoke the find_customer_context tool and return the raw envelope.

    Passes only the non-None lookup parameters so the tool call is minimal
    (§9.2 — no over-fetching).

    Args:
        phone_e164: E.164 phone number lookup key, if available.
        email: Email lookup key, if available.
        caller_id: Caller-ID lookup key, if available.
        booking_reference: Booking-reference lookup key, if available.

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    params: dict[str, str] = {}
    if phone_e164 is not None:
        params["phone_e164"] = phone_e164
    if email is not None:
        params["email"] = email
    if caller_id is not None:
        params["caller_id"] = caller_id
    if booking_reference is not None:
        params["booking_reference"] = booking_reference
    return await find_customer_context.ainvoke(params)


def parse_customer_context(env: ToolResultEnvelope) -> CustomerContextView | None:
    """Parse a ``CustomerContextView`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`discover_customer_context`.

    Returns:
        A validated ``CustomerContextView`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None``.
    """
    if not env.ok or env.data is None:
        _log.warning(
            "discover_customer_context returned non-ok or empty data: %s", env.status
        )
        return None
    try:
        return CustomerContextView.model_validate(
            _payload(env.data, CustomerContextView)
        )
    except Exception:
        _log.exception("Failed to parse CustomerContextView from envelope data")
        return None


# ---------------------------------------------------------------------------
# validate_booking_draft wrapper
# ---------------------------------------------------------------------------


async def validate_draft(draft: BookingDraft) -> ToolResultEnvelope:
    """Invoke the validate_booking_draft tool and return the raw envelope.

    Args:
        draft: The ``BookingDraft`` to validate against tenant policy.

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    return await validate_booking_draft.ainvoke(draft.model_dump(mode="json"))


def parse_policy_validation(env: ToolResultEnvelope) -> PolicyValidationResult | None:
    """Parse a ``PolicyValidationResult`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`validate_draft`.

    Returns:
        A validated ``PolicyValidationResult`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None``.
    """
    if not env.ok or env.data is None:
        _log.warning("validate_draft returned non-ok or empty data: %s", env.status)
        return None
    try:
        return PolicyValidationResult.model_validate(
            _payload(env.data, PolicyValidationResult)
        )
    except Exception:
        _log.exception("Failed to parse PolicyValidationResult from envelope data")
        return None


# ---------------------------------------------------------------------------
# check_candidate_availability wrapper
# ---------------------------------------------------------------------------


async def evaluate_candidates(req: AvailabilityRequest) -> ToolResultEnvelope:
    """Invoke the check_candidate_availability tool and return the raw envelope.

    Args:
        req: The ``AvailabilityRequest`` to send (includes draft + hold preferences).

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    return await check_candidate_availability.ainvoke(req.model_dump(mode="json"))


def parse_availability(env: ToolResultEnvelope) -> AvailabilityResult | None:
    """Parse an ``AvailabilityResult`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`evaluate_candidates`.

    Returns:
        A validated ``AvailabilityResult`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None``.
    """
    if not env.ok or env.data is None:
        _log.warning(
            "evaluate_candidates returned non-ok or empty data: %s", env.status
        )
        return None
    try:
        return AvailabilityResult.model_validate(_payload(env.data, AvailabilityResult))
    except Exception:
        _log.exception("Failed to parse AvailabilityResult from envelope data")
        return None
