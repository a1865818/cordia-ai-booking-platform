"""11 foreground tool definitions bound for the LangGraph agent (spec 03 §4.3).

Each function delegates to EXACTLY ONE ``get_tool_client().call_tool(...)`` call.
No branching business logic, no direct transport/db/provider imports live here — the
ToolClient owns transport.

Spec refs: 03 §4.3 (tool contracts), §3 (context injection), §15 (Inv-13 gate),
Inv-1, Inv-2 (verbatim operation_id/idempotency_key), Inv-5/Inv-9 (dedup),
Inv-16 (no 'booked' wording in accepted hint).
"""

from __future__ import annotations

from langchain_core.tools import tool

from cordia_agent.tools._external_stubs import BookingDraft
from cordia_agent.tools.client import get_tool_client
from cordia_agent.tools.contracts import (
    AvailabilityRequest,
    HandoffRequest,
    SubmitConfirmedIntentInput,
    ToolResultEnvelope,
    VerificationRequest,
)


# ---------------------------------------------------------------------------
# 1. get_tenant_context — PURE_READ, exempt from Inv-13 gate in client
# ---------------------------------------------------------------------------


@tool
async def get_tenant_context() -> ToolResultEnvelope:
    """Retrieve the resolved tenant context including timezone, policy version, and supported capabilities. Call this once at the start of every session before any data tool."""
    return await get_tool_client().call_tool(
        "get_tenant_context",
        "GET",
        "/api/tenant-context",
    )


# ---------------------------------------------------------------------------
# 2. find_customer_context — PURE_READ, query-string params
# ---------------------------------------------------------------------------


@tool
async def find_customer_context(
    phone_e164: str | None = None,
    email: str | None = None,
    caller_id: str | None = None,
    booking_reference: str | None = None,
) -> ToolResultEnvelope:
    """Look up an existing customer record by phone, email, caller ID, or booking reference. Provide at least one identifier; returns customer preferences and payment token status."""
    params: dict[str, str] = {}
    if phone_e164 is not None:
        params["phone_e164"] = phone_e164
    if email is not None:
        params["email"] = email
    if caller_id is not None:
        params["caller_id"] = caller_id
    if booking_reference is not None:
        params["booking_reference"] = booking_reference
    return await get_tool_client().call_tool(
        "find_customer_context",
        "GET",
        "/api/customers/context",
        params=params if params else None,
    )


# ---------------------------------------------------------------------------
# 3. search_vendor_catalog — PURE_READ, query-string params
# ---------------------------------------------------------------------------


@tool
async def search_vendor_catalog(
    query: str,
    category: str | None = None,
    max_results: int | None = None,
) -> ToolResultEnvelope:
    """Search the vendor's service catalog by keyword query, optional category filter, and result limit. Use to present available services to the customer."""
    params: dict[str, str | int] = {"query": query}
    if category is not None:
        params["category"] = category
    if max_results is not None:
        params["max_results"] = max_results
    return await get_tool_client().call_tool(
        "search_vendor_catalog",
        "GET",
        "/api/catalog/items",
        params=params,
    )


# ---------------------------------------------------------------------------
# 4. get_catalog_item — PURE_READ, path param
# ---------------------------------------------------------------------------


@tool
async def get_catalog_item(item_id: str) -> ToolResultEnvelope:
    """Fetch full details for a single catalog item by its stable item_id. Use after search to confirm pricing or description before building a booking draft."""
    return await get_tool_client().call_tool(
        "get_catalog_item",
        "GET",
        f"/api/catalog/items/{item_id}",
    )


# ---------------------------------------------------------------------------
# 5. validate_booking_draft — PURE_READ, JSON body
# ---------------------------------------------------------------------------


@tool(args_schema=BookingDraft)
async def validate_booking_draft(
    **kwargs: object,
) -> ToolResultEnvelope:
    """Validate a booking draft against tenant policy before requesting availability or submitting. Returns recoverable_fields and suggested_action when invalid."""
    draft = BookingDraft(**kwargs)
    return await get_tool_client().call_tool(
        "validate_booking_draft",
        "POST",
        "/api/booking-drafts/validate",
        json=draft.model_dump(mode="json"),
    )


# ---------------------------------------------------------------------------
# 6. check_candidate_availability — BOUNDED_SIDE_EFFECT
# ---------------------------------------------------------------------------


@tool(args_schema=AvailabilityRequest)
async def check_candidate_availability(
    **kwargs: object,
) -> ToolResultEnvelope:
    """Check slot availability for a booking draft and optionally hold a candidate slot. Pass hold_idempotency_key when want_hold=True to prevent duplicate holds (Inv-5)."""
    req = AvailabilityRequest(**kwargs)
    return await get_tool_client().call_tool(
        "check_candidate_availability",
        "POST",
        "/api/availability/candidates",
        json=req.model_dump(mode="json"),
        idempotency_key=req.hold_idempotency_key,
    )


# ---------------------------------------------------------------------------
# 7. request_verification — BOUNDED_SIDE_EFFECT, dedup-keyed (Inv-9)
# ---------------------------------------------------------------------------


@tool(args_schema=VerificationRequest)
async def request_verification(
    **kwargs: object,
) -> ToolResultEnvelope:
    """Send a verification code to the customer via SMS or email for the specified action. Pass a stable verification_idempotency_key — repeated calls with the same key return already_sent without re-sending (Inv-9)."""
    req = VerificationRequest(**kwargs)
    return await get_tool_client().call_tool(
        "request_verification",
        "POST",
        "/api/verifications",
        json=req.model_dump(mode="json"),
        idempotency_key=req.verification_idempotency_key,
    )


# ---------------------------------------------------------------------------
# 8. check_verification — PURE_READ; GET status or POST /verify with code
# ---------------------------------------------------------------------------


@tool
async def check_verification(
    verification_id: str,
    code: str | None = None,
) -> ToolResultEnvelope:
    """Check the status of a pending verification or submit the customer's code. Provide verification_id always; add code to submit and attempt grant issuance."""
    if code is not None:
        return await get_tool_client().call_tool(
            "check_verification",
            "POST",
            f"/api/verifications/{verification_id}/verify",
            json={"code": code},
        )
    return await get_tool_client().call_tool(
        "check_verification",
        "GET",
        f"/api/verifications/{verification_id}",
    )


# ---------------------------------------------------------------------------
# 9. submit_confirmed_intent — DURABLE_MUTATION; Inv-2 verbatim passthrough
# ---------------------------------------------------------------------------


@tool(args_schema=SubmitConfirmedIntentInput)
async def submit_confirmed_intent(
    **kwargs: object,
) -> ToolResultEnvelope:
    """Submit the customer's confirmed booking intent as a durable mutation. operation_id and idempotency_key are injected verbatim — never re-minted (Inv-2). The accepted response will confirm submission, not use 'booked' language (Inv-16)."""
    inp = SubmitConfirmedIntentInput(**kwargs)
    return await get_tool_client().call_tool(
        "submit_confirmed_intent",
        "POST",
        "/api/bookings",
        json=inp.model_dump(mode="json"),
        operation_id=inp.operation_id,
        idempotency_key=inp.idempotency_key,
    )


# ---------------------------------------------------------------------------
# 10. get_operation_status — PURE_READ, path param
# ---------------------------------------------------------------------------


@tool
async def get_operation_status(operation_id: str) -> ToolResultEnvelope:
    """Poll the status of a durable booking operation by its operation_id. Use after submit_confirmed_intent to determine when the operation reaches a terminal state."""
    return await get_tool_client().call_tool(
        "get_operation_status",
        "GET",
        f"/api/booking-operations/{operation_id}",
    )


# ---------------------------------------------------------------------------
# 11. request_human_handoff — BOUNDED_SIDE_EFFECT, dedup-keyed (Inv-5)
# ---------------------------------------------------------------------------


@tool(args_schema=HandoffRequest)
async def request_human_handoff(
    **kwargs: object,
) -> ToolResultEnvelope:
    """Escalate the current session to a human agent by creating a handoff work item. Pass a stable handoff_idempotency_key — repeated calls with the same key return existing without creating duplicates (Inv-5)."""
    req = HandoffRequest(**kwargs)
    return await get_tool_client().call_tool(
        "request_human_handoff",
        "POST",
        "/api/handoffs",
        json=req.model_dump(mode="json"),
        idempotency_key=req.handoff_idempotency_key,
    )


# ---------------------------------------------------------------------------
# Registry — exactly 11 tools; each .name equals the spec name above.
# ---------------------------------------------------------------------------

FOREGROUND_TOOLS: list = [
    get_tenant_context,
    find_customer_context,
    search_vendor_catalog,
    get_catalog_item,
    validate_booking_draft,
    check_candidate_availability,
    request_verification,
    check_verification,
    submit_confirmed_intent,
    get_operation_status,
    request_human_handoff,
]
