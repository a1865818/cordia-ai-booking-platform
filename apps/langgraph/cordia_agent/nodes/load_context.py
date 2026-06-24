"""Node: load_context — §8.2 Context Loading.

Loads tenant policy and customer context via the :class:`ReadContextClient`
port (faked until 02-H binds the concrete tool client).

Minimization rule (§9.2): if ``AgentState.verification_grant_id`` is ``None``,
sensitive history (``recent_booking_summaries``) is dropped before the context
is stored on state — "no sensitive history without a verification grant".

The read-client port mirrors the 02-D verifier-port pattern:
``register_read_client`` / ``get_read_client`` / ``clear_read_client``.
Production registers the real client at startup; tests register a fake.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Protocol, runtime_checkable


if TYPE_CHECKING:
    from cordia_agent.state import AgentState, CustomerContext, CustomerRef


# ---------------------------------------------------------------------------
# ReadContextClient port
# ---------------------------------------------------------------------------


@runtime_checkable
class ReadContextClient(Protocol):
    """Port: abstracts read-tool calls from production nodes.

    Production registers a concrete HTTP-backed client (02-H).
    Tests register :class:`tests._stubs.read_tools_fake.FakeReadContextClient`.
    """

    def get_tenant_context(self, tenant_id: str) -> dict:
        """Return a minimized tenant policy dict for *tenant_id*.

        Args:
            tenant_id: The tenant whose policy to load.

        Returns:
            A dict of tenant policy/configuration data (schema TBD in 03-B).
        """
        ...

    def find_customer_context(
        self, tenant_id: str, customer: CustomerRef | None
    ) -> CustomerContext:
        """Return a :class:`~cordia_agent.state.CustomerContext` for *customer*.

        The returned context MAY include ``recent_booking_summaries`` (sensitive
        history).  The node minimizes the result before storing it (§9.2).

        Args:
            tenant_id: Tenant scope (Inv-13).
            customer: Customer reference from the current session, or ``None``
                for anonymous sessions.

        Returns:
            A populated :class:`~cordia_agent.state.CustomerContext` instance.
        """
        ...


_read_client: ReadContextClient | None = None


def register_read_client(client: ReadContextClient) -> None:
    """Register the concrete read-context client for this process.

    Must be called once at startup (or in a test fixture) before
    :func:`load_context` is invoked.

    Args:
        client: The :class:`ReadContextClient` implementation to register.
    """
    global _read_client
    _read_client = client


def get_read_client() -> ReadContextClient:
    """Return the registered read client, or raise if none has been registered.

    Raises:
        RuntimeError: if :func:`register_read_client` has not been called yet.
    """
    if _read_client is None:
        raise RuntimeError("no ReadContextClient registered")
    return _read_client


def clear_read_client() -> None:
    """Remove the registered read client — call in test teardown to isolate tests."""
    global _read_client
    _read_client = None


# ---------------------------------------------------------------------------
# Node
# ---------------------------------------------------------------------------


async def load_context(state: AgentState) -> dict:
    """Load tenant policy + customer context; minimize sensitive history (§8.2 / §9.2).

    Calls the registered :class:`ReadContextClient` to fetch:

    * Tenant policy/configuration (result not yet stored on state — 03-B lands
      the typed model; stored for future use once that ticket ships).
    * Customer context, including potentially sensitive booking history.

    The context is then **minimized** before being stored on ``AgentState``:
    if ``state.verification_grant_id`` is ``None``, ``recent_booking_summaries``
    is cleared (§9.2 — no sensitive history without a verification grant).

    Args:
        state: Current ``AgentState``; ``tenant_id`` and ``draft.customer``
            provide the read-client parameters.

    Returns:
        A partial ``AgentState`` update dict containing ``customer_context``.
    """
    client = get_read_client()
    tenant_id = state.tenant_id or ""

    # Derive customer reference from the draft if available.
    customer: CustomerRef | None = state.draft.customer if state.draft else None

    # Fetch full context (may include sensitive history).
    full_context: CustomerContext = client.find_customer_context(tenant_id, customer)

    # Minimization (§9.2): drop sensitive history without a verification grant.
    if state.verification_grant_id is None:
        minimized = full_context.model_copy(update={"recent_booking_summaries": []})
    else:
        minimized = full_context

    return {"customer_context": minimized}
