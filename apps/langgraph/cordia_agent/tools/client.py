"""Typed HTTP tool client — single thin transport for every agent tool.

Spec refs: 03 §3.3 (context injection), §6.6 (latency/deadlines), §4.2 (envelope),
Inv-2 (operation_id/idempotency_key verbatim), Inv-5 (retry), Inv-13 (tenant gate),
Inv-14/Inv-16 (safe error mapping).

Design:
- One ``ToolClient`` instance is shared across a session and accepts an injected
  ``httpx.AsyncClient`` so tests can pass a ``MockTransport``-backed client without
  any real network.
- The tenant context is set once (post-resolution) via ``set_tenant_context``.
  Until then, every data tool call short-circuits with a ``tenant_not_resolved``
  envelope (Inv-13) — the transport is NEVER hit.
- Per-tool deadlines and retry eligibility are looked up from ``_timeouts``.
  PURE_READ tools retry exactly once on timeout/transport error; all others never
  retry (Inv-5).
- ``operation_id`` and ``idempotency_key`` are injected VERBATIM from caller args
  (Inv-2) — this client never re-mints them.
- Non-2xx responses and transport failures map to typed ``ToolResultEnvelope``
  instances via ``errors`` — raw body text NEVER reaches ``customer_hint``
  (Inv-14/Inv-16).
"""

from __future__ import annotations

import uuid
from typing import TYPE_CHECKING, Any

import httpx

from cordia_agent.tools._timeouts import (
    TOOL_DEADLINES,
    TOOL_SIDE_EFFECT_CLASS,
    SideEffectClass,
    is_retryable,
)
from cordia_agent.tools.contracts import SCHEMA_VERSION, ToolResultEnvelope
from cordia_agent.tools.errors import (
    map_http_error,
    map_transport_failure,
    tenant_not_resolved_envelope,
)


if TYPE_CHECKING:
    from cordia_agent.safety.tenant_context import TenantContext


class ToolClient:
    """Typed HTTP client for all 17 agent tools (spec 03 §3.3 / §6.6).

    Injected via DI so tests can pass a ``MockTransport``-backed
    ``httpx.AsyncClient`` — no real network is ever touched in tests.

    Invariants enforced:
    - Inv-13: data tools return ``tenant_not_resolved`` without touching the
      transport when ``tenant_context`` is ``None``.
    - Inv-2:  ``operation_id`` / ``idempotency_key`` are passed verbatim from
      caller args; the client never re-mints them.
    - Inv-5:  PURE_READ tools retry exactly once on timeout/transport error;
      BOUNDED_SIDE_EFFECT and DURABLE_MUTATION tools never retry.
    - Inv-14/Inv-16: raw response body and exception text never appear in
      ``customer_hint``.
    """

    def __init__(
        self,
        http: httpx.AsyncClient,
        *,
        tenant_context: TenantContext | None = None,
    ) -> None:
        """Initialise the client with an injected HTTP transport.

        Args:
            http: An ``httpx.AsyncClient`` — may be backed by a
                ``MockTransport`` in tests.
            tenant_context: Optional resolved ``TenantContext``; may be set
                later via ``set_tenant_context``.
        """
        self._http = http
        self._tenant_context = tenant_context

    def set_tenant_context(self, ctx: TenantContext) -> None:
        """Store the resolved ``TenantContext`` for subsequent calls.

        Called once by the graph after ``resolve_tenant`` succeeds.

        Args:
            ctx: The verified, immutable ``TenantContext`` from spec-15.
        """
        self._tenant_context = ctx

    async def call_tool(
        self,
        tool_name: str,
        method: str,
        path: str,
        *,
        json: dict[str, Any] | None = None,
        params: dict[str, Any] | None = None,
        operation_id: str | None = None,
        idempotency_key: str | None = None,
    ) -> ToolResultEnvelope:
        """Execute a single tool HTTP request and return a typed envelope.

        Steps (spec 03 §3.3 / §6.6):
        1. Inv-13 gate — returns ``tenant_not_resolved`` without touching the
           transport for any data tool when no context has been set.
        2. Build headers — injects tenant/correlation IDs from the context and
           verbatim ``operation_id``/``idempotency_key`` for mutation tools
           (Inv-2).
        3. Look up the per-tool deadline and retry flag from ``_timeouts``.
        4. Attempt the request; on timeout/transport error retry exactly once for
           PURE_READ tools (Inv-5).
        5. Map non-2xx responses to a typed envelope via ``errors`` — raw body
           text NEVER reaches ``customer_hint`` (Inv-14).

        Args:
            tool_name: One of the 17 canonical tool names (keys in
                ``TOOL_DEADLINES``).
            method: HTTP method string, e.g. ``"GET"``, ``"POST"``.
            path: URL path relative to the client ``base_url``.
            json: Optional request body serialised as JSON.
            params: Optional URL query-string parameters dict (for GET tools
                that accept query params, e.g. find_customer_context).
                Default ``None`` — no query string appended.
            operation_id: Durable-mutation operation identifier — injected
                verbatim as ``X-Operation-Id`` (Inv-2).  Never re-minted.
            idempotency_key: Request idempotency key — injected verbatim as
                ``X-Idempotency-Key`` (Inv-2).  Never re-minted.

        Returns:
            A ``ToolResultEnvelope`` with ``ok=True`` on success, or
            ``ok=False`` with a safe ``customer_hint`` on any error.
        """
        # ------------------------------------------------------------------
        # 1. Inv-13 gate: refuse data tools when tenant context is absent.
        # ------------------------------------------------------------------
        ctx = self._tenant_context

        # A per-call fallback correlation_id when there is no context.
        fallback_correlation_id = str(uuid.uuid4())

        if tool_name != "get_tenant_context" and ctx is None:
            return tenant_not_resolved_envelope(
                tool_name=tool_name,
                correlation_id=fallback_correlation_id,
            )

        # ------------------------------------------------------------------
        # 2. Build request headers.
        # ------------------------------------------------------------------
        correlation_id = (
            ctx.correlation_id if ctx is not None else fallback_correlation_id
        )

        headers: dict[str, str] = {
            "X-Correlation-Id": correlation_id,
            # Per-call causation ID — always a fresh UUID4.
            "X-Causation-Id": str(uuid.uuid4()),
        }

        if ctx is not None:
            headers["X-Tenant-Id"] = ctx.tenant_id

        side_effect_class = TOOL_SIDE_EFFECT_CLASS[tool_name]

        if side_effect_class == SideEffectClass.DURABLE_MUTATION:
            # Verbatim injection — Inv-2: the client never re-mints these.
            if operation_id is not None:
                headers["X-Operation-Id"] = operation_id
            if idempotency_key is not None:
                headers["X-Idempotency-Key"] = idempotency_key
        elif side_effect_class == SideEffectClass.BOUNDED_SIDE_EFFECT:
            # Pass through caller idempotency key if supplied.
            if idempotency_key is not None:
                headers["X-Idempotency-Key"] = idempotency_key

        # ------------------------------------------------------------------
        # 3. Look up per-tool deadline and retry flag.
        # ------------------------------------------------------------------
        deadline = TOOL_DEADLINES[tool_name]
        retryable = is_retryable(tool_name)

        # ------------------------------------------------------------------
        # 4. Attempt the request; retry exactly once for PURE_READ tools.
        # ------------------------------------------------------------------
        response: httpx.Response | None = None
        last_transport_error: Exception | None = None

        for _attempt in range(2 if retryable else 1):
            try:
                response = await self._http.request(
                    method,
                    path,
                    json=json,
                    params=params,
                    headers=headers,
                    timeout=deadline,
                )
                last_transport_error = None
                break
            except (httpx.TimeoutException, httpx.TransportError) as exc:
                last_transport_error = exc
                if not retryable:
                    # Non-retryable — bail immediately after 1 attempt.
                    break
                # retryable: loop will try a second time if attempt == 0.

        if last_transport_error is not None or response is None:
            # All attempts exhausted — map to unavailable envelope (Inv-16).
            return map_transport_failure(
                tool_name=tool_name,
                correlation_id=correlation_id,
            )

        # ------------------------------------------------------------------
        # 5. Map the response to a typed envelope.
        # ------------------------------------------------------------------
        if response.status_code < 300:
            # 2xx — parse body as JSON dict where possible.
            try:
                body: Any = response.json()
            except Exception:
                body = None

            body_dict: dict[str, Any] | None = body if isinstance(body, dict) else None

            return ToolResultEnvelope(
                ok=True,
                status=(
                    body_dict.get("status", "ok") if body_dict is not None else "ok"
                )
                or "ok",
                data=body_dict,
                customer_hint=(
                    body_dict.get("customer_hint") if body_dict is not None else None
                ),
                correlation_id=correlation_id,
                schema_version=SCHEMA_VERSION,
            )

        # Non-2xx — safe error mapping; raw body NEVER read into hint (Inv-14).
        return map_http_error(
            response.status_code,
            tool_name=tool_name,
            correlation_id=correlation_id,
        )


# ---------------------------------------------------------------------------
# Module-level client registry (mirrors the port pattern used by other tickets).
# Defined AFTER ToolClient so the type annotation resolves at module load time.
# ---------------------------------------------------------------------------

_tool_client: ToolClient | None = None


def set_tool_client(client: ToolClient) -> None:
    """Register the session-scoped ``ToolClient`` instance.

    Called once during graph initialisation (spec 03 §3.3 / §4.2).

    Args:
        client: The configured ``ToolClient`` to make available to foreground tools.
    """
    global _tool_client
    _tool_client = client


def get_tool_client() -> ToolClient:
    """Return the registered ``ToolClient`` instance.

    Raises:
        RuntimeError: if no client has been registered via ``set_tool_client``.

    Returns:
        The session-scoped ``ToolClient``.
    """
    if _tool_client is None:
        raise RuntimeError("no ToolClient configured")
    return _tool_client


def reset_tool_client() -> None:
    """Clear the registered ``ToolClient`` (test teardown helper)."""
    global _tool_client
    _tool_client = None
