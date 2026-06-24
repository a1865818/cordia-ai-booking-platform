"""Safe error-to-envelope mapping for the typed HTTP tool client.

Spec refs: 03 §5.2 (conflict), §5.3 (rate-limit), §4.2 (envelope), Inv-14/Inv-16.

Rules:
- Raw HTTP response bodies NEVER appear in ``customer_hint`` (Inv-14).
- Raw exception messages NEVER appear in ``customer_hint`` (Inv-16).
- Every envelope has ``ok=False`` and a safe, generic customer-facing hint.
- ``data`` is always ``None`` for error envelopes — no provider payload leaks.
"""

from __future__ import annotations

from cordia_agent.tools.contracts import SCHEMA_VERSION, ToolResultEnvelope


# ---------------------------------------------------------------------------
# Safe, generic customer hints — no raw provider text (Inv-14/Inv-16).
# ---------------------------------------------------------------------------

_HINT_CONFLICT = "That request conflicts with the current state of your booking."
_HINT_RATE_LIMITED = (
    "We're receiving a lot of requests right now; please try again shortly."
)
_HINT_UNAVAILABLE = "I'm having trouble reaching the system right now."
_HINT_NOT_FOUND = "The requested resource could not be found."
_HINT_TENANT_NOT_RESOLVED = "I'm having trouble reaching this business right now."


def map_http_error(
    status_code: int,
    *,
    tool_name: str,
    correlation_id: str,
) -> ToolResultEnvelope:
    """Map a non-2xx HTTP status code to a typed ``ToolResultEnvelope``.

    The raw response body is NOT read and NEVER included in ``customer_hint``
    (Inv-14).  Only the status code is used to select the safe hint.

    Args:
        status_code: The HTTP response status code (e.g. 409, 429, 500).
        tool_name: The tool that received the error (for logging correlation).
        correlation_id: Propagated correlation ID for observability.

    Returns:
        A ``ToolResultEnvelope`` with ``ok=False``, a safe ``status`` string,
        and a generic ``customer_hint``.
    """
    if status_code == 404:
        status = "not_found"
        hint = _HINT_NOT_FOUND
    elif status_code == 409:
        status = "conflict"
        hint = _HINT_CONFLICT
    elif status_code == 429:
        status = "rate_limited"
        hint = _HINT_RATE_LIMITED
    elif status_code >= 500:
        status = "unavailable"
        hint = _HINT_UNAVAILABLE
    elif status_code >= 400:
        # Catch-all for other 4xx (400, 401, 403, 422, etc.)
        status = "request_error"
        hint = _HINT_UNAVAILABLE
    else:
        # Should not be reachable — only called for non-2xx.
        status = "unavailable"
        hint = _HINT_UNAVAILABLE

    return ToolResultEnvelope(
        ok=False,
        status=status,
        customer_hint=hint,
        data=None,
        correlation_id=correlation_id,
        schema_version=SCHEMA_VERSION,
    )


def map_transport_failure(
    *,
    tool_name: str,
    correlation_id: str,
) -> ToolResultEnvelope:
    """Map a timeout or transport-layer error to a typed ``ToolResultEnvelope``.

    The raw exception message NEVER appears in ``customer_hint`` (Inv-16).

    Args:
        tool_name: The tool whose request failed (for logging correlation).
        correlation_id: Propagated correlation ID for observability.

    Returns:
        A ``ToolResultEnvelope`` with ``ok=False``, ``status="unavailable"``,
        and the generic unavailability hint.
    """
    return ToolResultEnvelope(
        ok=False,
        status="unavailable",
        customer_hint=_HINT_UNAVAILABLE,
        data=None,
        correlation_id=correlation_id,
        schema_version=SCHEMA_VERSION,
    )


def tenant_not_resolved_envelope(
    *,
    tool_name: str,
    correlation_id: str,
) -> ToolResultEnvelope:
    """Return a ``tenant_not_resolved`` envelope when the Inv-13 gate fires.

    Called by ``ToolClient.call_tool`` before touching the transport when no
    verified ``TenantContext`` has been set and the tool is not the
    ``get_tenant_context`` resolver (spec 03 §3, Inv-13).

    Args:
        tool_name: The data tool that was called without a resolved context.
        correlation_id: Best-effort correlation ID (may be a generated fallback).

    Returns:
        A ``ToolResultEnvelope`` with ``ok=False``,
        ``status="tenant_not_resolved"``, and the tenant-hint string.
    """
    return ToolResultEnvelope(
        ok=False,
        status="tenant_not_resolved",
        customer_hint=_HINT_TENANT_NOT_RESOLVED,
        data=None,
        correlation_id=correlation_id,
        schema_version=SCHEMA_VERSION,
    )
