"""Node: resolve_tenant — §8.1 Tenant Resolution (§8.3 gate, Inv-13).

Verifies the signed TenantContext token before any data-access node runs.
On success, populates AgentState with tenant identity fields derived SOLELY
from the verified context.  On failure, raises ``InvalidTenantContext`` so
the graph cannot advance to ``load_context`` (hard gate, no extra edges).
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from cordia_agent.safety.tenant_context import (
    InvalidTenantContext,
    verify_signed_context,
)


if TYPE_CHECKING:
    from cordia_agent.state import AgentState


async def resolve_tenant(state: AgentState) -> dict:
    """Verify the signed tenant context and populate AgentState (§8.1 / Inv-13).

    Reads tenant identity ONLY from the verified context — the plain input
    ``tenant_id`` field has been removed from ``AgentInputState`` to prevent
    Inv-13 bypass.

    Raises:
        InvalidTenantContext: if ``signed_tenant_context`` is absent/empty or
            the verifier rejects the token (bad signature, tampered payload,
            etc.).  The exception halts graph execution before any downstream
            data-access node runs.
    """
    token = state.signed_tenant_context
    if not token:
        raise InvalidTenantContext(
            "signed_tenant_context is absent or empty; cannot resolve tenant (Inv-13)"
        )

    # May raise InvalidTenantContext — intentionally propagated to halt the graph.
    ctx = verify_signed_context(token)

    return {
        "tenant_id": ctx.tenant_id,
        "tenant_policy_version": ctx.configuration_version,
        "correlation_id": ctx.correlation_id,
        "channel_endpoint_id": ctx.channel_endpoint_id,
        "allowed_policy_ids": ctx.allowed_policy_ids,
        "tenant_context_verified": True,
    }
