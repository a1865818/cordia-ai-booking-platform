"""Per-tool deadline and side-effect-class tables for the typed HTTP tool client.

Spec refs: 03 §6.6 (latency budgets), §7 (tool categorisation by side-effect class).

These tables drive two behaviours in ``ToolClient``:
- ``TOOL_DEADLINES``          — httpx timeout (seconds) passed to each request.
- ``TOOL_SIDE_EFFECT_CLASS``  — determines retry eligibility (Inv-5):
    * PURE_READ       → exactly one retry on timeout/transport error.
    * BOUNDED_SIDE_EFFECT → no retry.
    * DURABLE_MUTATION    → no retry.
"""

from __future__ import annotations

from enum import Enum


class SideEffectClass(str, Enum):
    """Categorisation of a tool's side-effect scope (spec 03 §7)."""

    PURE_READ = "pure_read"
    BOUNDED_SIDE_EFFECT = "bounded_side_effect"
    DURABLE_MUTATION = "durable_mutation"


# ---------------------------------------------------------------------------
# Per-tool deadline table (seconds) — ALL 17 tools (spec 03 §6.6).
# ---------------------------------------------------------------------------

TOOL_DEADLINES: dict[str, float] = {
    "get_tenant_context": 0.08,
    "find_customer_context": 0.12,
    "search_vendor_catalog": 0.15,
    "get_catalog_item": 0.15,
    "validate_booking_draft": 0.10,
    "check_candidate_availability": 0.40,
    "request_verification": 0.25,
    "check_verification": 0.12,
    "submit_confirmed_intent": 0.30,
    "get_operation_status": 0.12,
    "request_human_handoff": 0.20,
    "find_manageable_bookings": 0.15,
    "get_booking_details": 0.20,
    "validate_booking_change": 0.35,
    "submit_booking_modification": 0.30,
    "submit_booking_cancellation": 0.30,
    "get_booking_operation_status": 0.12,
}

# ---------------------------------------------------------------------------
# Per-tool side-effect-class table — ALL 17 tools (spec 03 §7).
# ---------------------------------------------------------------------------

TOOL_SIDE_EFFECT_CLASS: dict[str, SideEffectClass] = {
    # PURE_READ (11 tools) — safe to retry exactly once on timeout.
    "get_tenant_context": SideEffectClass.PURE_READ,
    "find_customer_context": SideEffectClass.PURE_READ,
    "search_vendor_catalog": SideEffectClass.PURE_READ,
    "get_catalog_item": SideEffectClass.PURE_READ,
    "validate_booking_draft": SideEffectClass.PURE_READ,
    "check_verification": SideEffectClass.PURE_READ,
    "get_operation_status": SideEffectClass.PURE_READ,
    "find_manageable_bookings": SideEffectClass.PURE_READ,
    "get_booking_details": SideEffectClass.PURE_READ,
    "validate_booking_change": SideEffectClass.PURE_READ,
    "get_booking_operation_status": SideEffectClass.PURE_READ,
    # BOUNDED_SIDE_EFFECT (3 tools) — no retry.
    "check_candidate_availability": SideEffectClass.BOUNDED_SIDE_EFFECT,
    "request_verification": SideEffectClass.BOUNDED_SIDE_EFFECT,
    "request_human_handoff": SideEffectClass.BOUNDED_SIDE_EFFECT,
    # DURABLE_MUTATION (3 tools) — no retry.
    "submit_confirmed_intent": SideEffectClass.DURABLE_MUTATION,
    "submit_booking_modification": SideEffectClass.DURABLE_MUTATION,
    "submit_booking_cancellation": SideEffectClass.DURABLE_MUTATION,
}

# ---------------------------------------------------------------------------
# Frozen set of all 17 tool names — used for coverage assertions.
# ---------------------------------------------------------------------------

ALL_TOOLS: frozenset[str] = frozenset(TOOL_DEADLINES)

# Internal consistency guard — both tables must cover exactly the same 17 tools.
assert set(TOOL_DEADLINES) == set(TOOL_SIDE_EFFECT_CLASS) == set(ALL_TOOLS), (
    "TOOL_DEADLINES and TOOL_SIDE_EFFECT_CLASS must have identical key sets."
)


def is_retryable(tool_name: str) -> bool:
    """Return True iff *tool_name* is a PURE_READ tool (safe to retry on timeout).

    Only PURE_READ tools are retried exactly once on ``httpx.TimeoutException``
    or ``httpx.TransportError`` (Inv-5).  BOUNDED_SIDE_EFFECT and
    DURABLE_MUTATION tools are never retried.

    Args:
        tool_name: One of the 17 canonical tool names.

    Returns:
        ``True`` for PURE_READ tools, ``False`` otherwise.

    Raises:
        KeyError: if *tool_name* is not in ``TOOL_SIDE_EFFECT_CLASS``.
    """
    return TOOL_SIDE_EFFECT_CLASS[tool_name] == SideEffectClass.PURE_READ
