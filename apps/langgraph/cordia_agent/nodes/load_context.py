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

import asyncio
import logging

from cordia_agent.state import AgentState, CustomerContext


_log = logging.getLogger(__name__)


async def load_context(state: AgentState) -> dict:
    """Load tenant policy + customer context via the 03 tool client (§8.2 / §9.2).

    Steps:
    1. Derives customer lookup args from ``state.customer`` (the channel's
       caller hint), falling back to ``state.draft.customer``.
    2. Calls ``client_binding.load_tenant_context()`` and
       ``client_binding.discover_customer_context()`` concurrently — neither
       depends on the other's result, and sequential awaits would add a full
       extra round trip to every turn. Sets ``tenant_policy_version`` on state
       when not already present.
    3. Maps the ``CustomerContextView`` (already minimized by the 03 contract)
       to a ``state.CustomerContext``; ``recent_booking_summaries`` is always
       empty here (§9.2 — sensitive history only with a verification grant).
    4. Handles non-ok envelopes gracefully: logs and leaves the field unset
       rather than crashing the graph.

    Args:
        state: Current ``AgentState``; ``customer`` and ``draft.customer``
            provide lookup hints.

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

    # The channel's caller hint first, the draft second — derived up front so
    # both tool calls below are independent and can run concurrently.  This
    # node runs on the FIRST turn, before identify_intent has built a draft,
    # so deriving the phone from state.draft.customer alone meant discovery
    # always ran with None and never recognised a returning caller.
    # state.customer is the channel-supplied CustomerRef (telephony ANI) and is
    # populated from turn one, so it is the primary source; the draft is kept
    # as a fallback for whatever the customer volunteered later in the dialog.
    phone_e164: str | None = state.customer.phone_e164 if state.customer else None

    if phone_e164 is None and state.draft is not None:
        draft_customer = state.draft.customer
        if draft_customer is not None:
            phone_e164 = draft_customer.phone_e164

    # Neither call depends on the other's result, so they run concurrently:
    # sequential awaits here cost a full extra round trip on every turn.
    tenant_env, customer_env = await asyncio.gather(
        load_tenant_context(), discover_customer_context(phone_e164=phone_e164)
    )

    # ------------------------------------------------------------------
    # 1. Tenant context — sets policy_version when not already resolved.
    # ------------------------------------------------------------------
    tenant_view = parse_tenant_context(tenant_env)

    if tenant_view is not None:
        # Only update if not already stamped (resolve_tenant may have set it).
        if not state.tenant_policy_version:
            result["tenant_policy_version"] = tenant_view.policy_version
        # The venue's zone is what makes a spoken time an instant.  Carried on
        # state rather than re-fetched: capture_fields needs it every turn, and a
        # second lookup per turn buys nothing on a call with a latency budget.
        result["tenant_timezone"] = tenant_view.timezone
        # Opening hours ride along for the same reason: answer_inquiry needs
        # them to say when we close, and clarify needs them to say we're shut.
        result["tenant_hours"] = (
            [h.model_dump(mode="json") for h in tenant_view.hours]
            if tenant_view.hours
            else None
        )
    else:
        _log.warning(
            "load_context: tenant context unavailable (status=%s); "
            "continuing without updating policy_version",
            tenant_env.status,
        )

    # ------------------------------------------------------------------
    # 2. Customer context.
    # ------------------------------------------------------------------
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
