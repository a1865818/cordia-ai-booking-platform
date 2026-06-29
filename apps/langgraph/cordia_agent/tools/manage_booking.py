"""6 manage-booking tool definitions bound for the LangGraph agent (spec 03 §4.4).

Each function delegates to EXACTLY ONE ``get_tool_client().call_tool(...)`` call.
No branching business logic, no direct transport/db/provider imports live here —
the ToolClient owns transport and the grant gate (Inv-11).

Spec refs: 03 §4.4 (tool contracts), §9.4 (manage-booking reads),
Inv-2 (verbatim operation_id/idempotency_key), Inv-7 (mutations not retried),
Inv-11 (grant gate for mutations), Inv-16 (NormalizedOperationStatus).
"""

from __future__ import annotations

from langchain_core.tools import tool

from cordia_agent.tools.client import get_tool_client
from cordia_agent.tools.contracts import (
    SubmitCancellationInput,
    SubmitModificationInput,
    ToolResultEnvelope,
)


# ---------------------------------------------------------------------------
# 1. find_manageable_bookings — PURE_READ, query-string params
# ---------------------------------------------------------------------------


@tool
async def find_manageable_bookings(
    phone_e164: str | None = None,
    booking_reference: str | None = None,
    email: str | None = None,
) -> ToolResultEnvelope:
    """Find bookings that the authenticated customer can manage (modify or cancel). Provide at least one of phone_e164, booking_reference, or email to identify the customer. Returns a minimised list of manageable bookings (§9.4.1)."""
    params: dict[str, str] = {}
    if phone_e164 is not None:
        params["phone_e164"] = phone_e164
    if booking_reference is not None:
        params["booking_reference"] = booking_reference
    if email is not None:
        params["email"] = email
    return await get_tool_client().call_tool(
        "find_manageable_bookings",
        "GET",
        "/api/bookings",
        params=params if params else None,
    )


# ---------------------------------------------------------------------------
# 2. get_booking_details — PURE_READ, optional grant for fuller projection
# ---------------------------------------------------------------------------


@tool
async def get_booking_details(
    booking_id: str,
    grant_id: str | None = None,
) -> ToolResultEnvelope:
    """Fetch details for a single booking by its stable booking_id. Without a grant_id only the non-sensitive disambiguator fields are returned (§9.4.1). With a valid grant_id a fuller view is returned. Pass the grant_id when the customer has been verified."""
    return await get_tool_client().call_tool(
        "get_booking_details",
        "GET",
        f"/api/bookings/{booking_id}",
        grant_id=grant_id,
    )


# ---------------------------------------------------------------------------
# 3. validate_booking_change — PURE_READ, JSON body
# ---------------------------------------------------------------------------


@tool
async def validate_booking_change(
    target_booking_id: str,
    expected_booking_version: int,
    requested_fields: dict,
    grant_id: str,
) -> ToolResultEnvelope:
    """Validate a proposed booking change against tenant policy before submitting a modification. Returns valid/invalid outcome, optional fees, and whether explicit confirmation is required (§9.4.3). grant_id is required to authorize the validation check."""
    return await get_tool_client().call_tool(
        "validate_booking_change",
        "POST",
        f"/api/bookings/{target_booking_id}/changes:validate",
        json={
            "target_booking_id": target_booking_id,
            "expected_booking_version": expected_booking_version,
            "requested_fields": requested_fields,
            "grant_id": grant_id,
        },
    )


# ---------------------------------------------------------------------------
# 4. submit_booking_modification — DURABLE_MUTATION, grant REQUIRED (Inv-11)
# ---------------------------------------------------------------------------


@tool(args_schema=SubmitModificationInput)
async def submit_booking_modification(
    **kwargs: object,
) -> ToolResultEnvelope:
    """Submit a durable booking modification. operation_id and modification_idempotency_key are injected verbatim — never re-minted (Inv-2). grant_id is required to authorize the mutation (Inv-11). Stale-version conflicts surface as stale_version status — the tool never retries (Inv-7)."""
    inp = SubmitModificationInput(**kwargs)
    return await get_tool_client().call_tool(
        "submit_booking_modification",
        "POST",
        f"/api/bookings/{inp.target_booking_id}/modifications",
        json=inp.model_dump(mode="json"),
        operation_id=inp.operation_id,
        idempotency_key=inp.modification_idempotency_key,
        grant_id=inp.grant_id,
    )


# ---------------------------------------------------------------------------
# 5. submit_booking_cancellation — DURABLE_MUTATION, grant REQUIRED (Inv-11)
# ---------------------------------------------------------------------------


@tool(args_schema=SubmitCancellationInput)
async def submit_booking_cancellation(
    **kwargs: object,
) -> ToolResultEnvelope:
    """Submit a durable booking cancellation. operation_id and cancellation_idempotency_key are injected verbatim — never re-minted (Inv-2). grant_id is required to authorize the mutation (Inv-11). already_cancelled and duplicate_accepted outcomes are surfaced, not retried (Inv-7)."""
    inp = SubmitCancellationInput(**kwargs)
    return await get_tool_client().call_tool(
        "submit_booking_cancellation",
        "POST",
        f"/api/bookings/{inp.target_booking_id}/cancellations",
        json=inp.model_dump(mode="json"),
        operation_id=inp.operation_id,
        idempotency_key=inp.cancellation_idempotency_key,
        grant_id=inp.grant_id,
    )


# ---------------------------------------------------------------------------
# 6. get_booking_operation_status — PURE_READ, path param
# ---------------------------------------------------------------------------


@tool
async def get_booking_operation_status(operation_id: str) -> ToolResultEnvelope:
    """Poll the status of a durable booking operation (modification or cancellation) by its operation_id. Returns a NormalizedOperationStatus-shaped status (Inv-16). Use after submit_booking_modification or submit_booking_cancellation to determine when the operation reaches a terminal state."""
    return await get_tool_client().call_tool(
        "get_booking_operation_status",
        "GET",
        f"/api/booking-operations/{operation_id}",
    )


# ---------------------------------------------------------------------------
# Registry — exactly 6 tools; each .name equals the spec name above.
# ---------------------------------------------------------------------------

MANAGE_BOOKING_TOOLS: list = [
    find_manageable_bookings,
    get_booking_details,
    validate_booking_change,
    submit_booking_modification,
    submit_booking_cancellation,
    get_booking_operation_status,
]
