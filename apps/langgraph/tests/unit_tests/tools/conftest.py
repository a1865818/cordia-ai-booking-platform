"""Shared fixtures for the typed HTTP tool client unit tests.

Provides:
- ``recording_transport_factory`` — creates a ``(ToolClient, captured_requests)``
  pair backed by an ``httpx.MockTransport`` so no real network is ever hit.
- ``resolved_tenant_context`` — a fake verified ``TenantContext`` instance
  suitable for passing to ``ToolClient``.
- Canned ``httpx.Response`` helpers for common HTTP status codes.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Any

import httpx
import pytest

from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.state import Channel
from cordia_agent.tools.client import ToolClient


if TYPE_CHECKING:
    from collections.abc import Callable


# ---------------------------------------------------------------------------
# Helpers — canned httpx.Response builders
# ---------------------------------------------------------------------------


def make_response(
    status_code: int,
    body: dict[str, Any] | None = None,
) -> httpx.Response:
    """Build a canned ``httpx.Response`` for use in ``MockTransport`` handlers.

    Args:
        status_code: HTTP status code, e.g. 200, 202, 409.
        body: Optional JSON body dict; defaults to ``{}``.

    Returns:
        An ``httpx.Response`` with JSON-encoded body.
    """
    import json

    raw_body = json.dumps(body or {}).encode()
    return httpx.Response(
        status_code=status_code,
        headers={"content-type": "application/json"},
        content=raw_body,
    )


CANNED_200 = make_response(200, {"status": "ok", "data": "result"})
CANNED_202 = make_response(202, {"status": "accepted"})
CANNED_409 = make_response(409, {"error": "conflict", "detail": "already exists"})
CANNED_429 = make_response(429, {"error": "rate_limited", "detail": "slow down"})
CANNED_500 = make_response(500, {"error": "internal_error", "detail": "server error"})


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture()
def resolved_tenant_context() -> TenantContext:
    """Return a fully-populated fake ``TenantContext`` (no real verifier needed).

    Uses direct model construction — bypasses the verifier port intentionally
    because client tests only need the data, not the signature mechanism.
    """
    return TenantContext(
        tenant_id="tenant_test_001",
        channel=Channel.voice,
        channel_endpoint_id="ep_test_001",
        configuration_version="v1",
        correlation_id="corr_test_abc123",
        allowed_policy_ids=["policy_a", "policy_b"],
    )


@pytest.fixture()
def client_factory(
    resolved_tenant_context: TenantContext,
) -> Callable[..., tuple[ToolClient, list[httpx.Request]]]:
    """Return a factory that creates ``(ToolClient, captured_requests)`` pairs.

    The factory signature::

        client_factory(
            response_or_handler,
            *,
            tenant_context=<resolved_tenant_context>,  # override to None for gate tests
        ) -> (ToolClient, captured_requests)

    ``response_or_handler`` may be:
    - An ``httpx.Response`` — returned for every request.
    - A callable ``(httpx.Request) -> httpx.Response`` — for dynamic responses.
    - An ``Exception`` subclass instance — raised for every request (e.g. timeout).

    Each request that reaches the transport is appended to ``captured_requests``
    BEFORE the response is produced (so raise-on-every-call still increments the
    count on each attempt).

    Args:
        resolved_tenant_context: Default tenant context injected by the fixture.

    Returns:
        A factory callable that produces ``(ToolClient, captured)`` pairs.
    """

    def factory(
        response_or_handler: Any,
        *,
        tenant_context: TenantContext | None = resolved_tenant_context,
    ) -> tuple[ToolClient, list[httpx.Request]]:
        captured: list[httpx.Request] = []

        def _handler(request: httpx.Request) -> httpx.Response:
            captured.append(request)

            if isinstance(response_or_handler, Exception):
                raise response_or_handler
            if callable(response_or_handler) and not isinstance(
                response_or_handler, httpx.Response
            ):
                return response_or_handler(request)
            return response_or_handler  # type: ignore[return-value]

        transport = httpx.MockTransport(_handler)
        http = httpx.AsyncClient(transport=transport, base_url="http://test")
        client = ToolClient(http, tenant_context=tenant_context)
        return client, captured

    return factory
