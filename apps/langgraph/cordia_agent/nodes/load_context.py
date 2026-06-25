"""Node: load_context — §8.2 Context Loading (02-H rewrite).

Loads tenant policy and customer context by calling the concrete 03 tool
wrappers in ``cordia_agent.tools.client_binding``.  The 02-F ``ReadContextClient``
port has been removed; this node now calls the live foreground-tool stack
(backed by a ``ToolClient`` / ``MockTransport`` in tests).

Minimization rule (§9.2): ``CustomerContextView.recent_booking_summaries`` is not
surfaced on this model — the discovery projection is already minimized by the
03 contract.  The mapped ``CustomerContext`` always has an empty
``recent_booking_summaries`` list; sensitive history is only fetched post-grant
(spec 03 §9.2 — no sensitive history without a verification grant).

Inv-13: all tool calls are tenant-scoped; ``resolve_tenant`` is guaranteed to have
run (and set tenant context on the ``ToolClient``) before this node executes.

Spec refs: spec 02 §8.2, §9.2; spec 03 §4.3; Inv-13.
"""

from __future__ import annotations

import logging

from cordia_agent.state import AgentState, CustomerContext


_log = logging.getLogger(__name__)


async def load_context(state: AgentState) -> dict:
    """Load tenant policy + customer context via the 03 tool client (§8.2 / §9.2).

    Steps:
    1. Calls ``client_binding.load_tenant_context()`` → sets
       ``tenant_policy_version`` on state when not already present.
    2. Derives customer lookup args from ``state.draft.customer`` (when present)
       and calls ``client_binding.discover_customer_context()``.
    3. Maps the ``CustomerContextView`` (already minimized by the 03 contract)
       to a ``state.CustomerContext``; ``recent_booking_summaries`` is always
       empty here (§9.2 — sensitive history only with a verification grant).
    4. Handles non-ok envelopes gracefully: logs and leaves the field unset
       rather than crashing the graph.

    Args:
        state: Current ``AgentState``; ``draft.customer`` provides lookup hints.

    Returns:
        A partial ``AgentState`` update dict containing zero or more of:
        ``tenant_policy_version``, ``customer_context``.
    """
    # Import here (inside function) keeps the module-level import graph clean
    # and avoids any top-level circular-import risk during graph construction.
    from cordia_agent.tools.client_binding import (
        discover_customer_context,
        load_tenant_context,
        parse_customer_context,
        parse_tenant_context,
    )

    result: dict = {}

    # ------------------------------------------------------------------
    # 1. Tenant context — sets policy_version when not already resolved.
    # ------------------------------------------------------------------
    tenant_env = await load_tenant_context()
    tenant_view = parse_tenant_context(tenant_env)

    if tenant_view is not None:
        # Only update if not already stamped (resolve_tenant may have set it).
        if not state.tenant_policy_version:
            result["tenant_policy_version"] = tenant_view.policy_version
    else:
        _log.warning(
            "load_context: tenant context unavailable (status=%s); "
            "continuing without updating policy_version",
            tenant_env.status,
        )

    # ------------------------------------------------------------------
    # 2. Customer context — derive lookup args from the draft if available.
    # ------------------------------------------------------------------
    customer_ref = state.draft.customer if state.draft else None

    phone_e164: str | None = None
    if customer_ref is not None:
        phone_e164 = customer_ref.phone_e164

    customer_env = await discover_customer_context(phone_e164=phone_e164)
    customer_view = parse_customer_context(customer_env)

    if customer_view is not None:
        # Map CustomerContextView → state.CustomerContext.
        # Discovery projection is already minimized (§9.2);
        # recent_booking_summaries is intentionally absent from CustomerContextView.
        result["customer_context"] = CustomerContext(
            customer_id=customer_view.customer_id,
            display_name=customer_view.display_name,
            recent_booking_summaries=[],  # §9.2 — never populated here
        )
    else:
        _log.warning(
            "load_context: customer context unavailable (status=%s); "
            "continuing with anonymous session",
            customer_env.status,
        )

    return result
