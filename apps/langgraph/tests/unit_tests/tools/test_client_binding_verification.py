"""Wrappers for the verification and manage-booking discovery tools (02-H).

``request_verification`` had no wrapper at all, so nothing in the graph could
ask for a code.  ``parse_verification_grant`` had one, and it validated the
whole response body — including the ``status``/``grant`` envelope the API
actually sends — against a model with ``extra="forbid"``, so a genuinely
granted response parsed as "no grant" and the caller was sent to a human.
"""

from __future__ import annotations

import datetime
import json
from typing import Any

import httpx
import pytest

from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.state import Channel
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from cordia_agent.tools.client_binding import (
    check_verification_call,
    find_manageable_bookings_call,
    get_booking_details_call,
    parse_booking_projection,
    parse_manageable_bookings,
    parse_verification_grant,
    parse_verification_sent,
    request_verification_call,
)
from cordia_agent.tools.contracts import VerificationRequest
from cordia_agent.tools.status_codes import VerificationAction


_TENANT_ID = "tenant_cb_test"

_FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id=_TENANT_ID,
    channel=Channel.voice,
    channel_endpoint_id="ep_cb_001",
    configuration_version="v1",
    correlation_id="corr_cb_001",
    session_id="sess_cb_001",
    allowed_agent_policy_ids=["policy_a"],
    allowed_tool_policy_ids=["policy_a"],
    issued_at="2026-07-25T00:00:00Z",
    expires_at="2026-07-25T01:00:00Z",
)


def _install(body: dict[str, Any], status_code: int = 200) -> list[httpx.Request]:
    seen: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return httpx.Response(
            status_code,
            headers={"content-type": "application/json"},
            content=json.dumps(body).encode(),
        )

    http = httpx.AsyncClient(
        transport=httpx.MockTransport(handler), base_url="http://test"
    )
    set_tool_client(ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT))
    return seen


@pytest.fixture(autouse=True)
def _reset_after_test():
    yield
    reset_tool_client()


@pytest.mark.unit
async def test_request_verification_posts_and_parses() -> None:
    seen = _install(
        {
            "status": "sent",
            "verification": {
                "verification_id": "verif_001",
                "channel": "sms",
                "expires_at": "2099-01-01T00:00:00+00:00",
                "masked_destination": "verified sms destination",
            },
        }
    )
    req = VerificationRequest(
        target_booking_id="bk_1",
        channel="sms",
        action=VerificationAction.CANCEL,
        verification_idempotency_key="verify:sess:cancel:0",
    )

    sent = parse_verification_sent(await request_verification_call(req))

    assert sent is not None
    assert sent.verification_id == "verif_001"
    assert seen[0].url.path == "/api/verifications"
    assert json.loads(seen[0].content)["verification_idempotency_key"] == (
        "verify:sess:cancel:0"
    )


@pytest.mark.unit
async def test_request_verification_without_a_destination_parses_to_none() -> None:
    _install({"status": "no_verified_destination", "verification": None})
    req = VerificationRequest(
        customer_id="cust_1",
        channel="sms",
        action=VerificationAction.CANCEL,
        verification_idempotency_key="k",
    )

    assert parse_verification_sent(await request_verification_call(req)) is None


@pytest.mark.unit
async def test_nested_grant_is_parsed() -> None:
    """The real CheckVerificationResponse nests the grant under ``grant``."""
    expires = (
        datetime.datetime.now(datetime.UTC) + datetime.timedelta(days=1)
    ).isoformat()
    _install(
        {
            "status": "granted",
            "grant": {
                "grant_id": "grant_001",
                "tenant_id": _TENANT_ID,
                "customer_id": "cust_001",
                "target_booking_id": "bk_1",
                "allowed_action": "cancel",
                "expires_at": expires,
                "scope": "operation",
            },
        }
    )

    grant = parse_verification_grant(
        await check_verification_call("verif_001", code="481502")
    )

    assert grant is not None
    assert grant.grant_id == "grant_001"
    assert grant.allowed_action is VerificationAction.CANCEL


@pytest.mark.unit
async def test_null_grant_parses_to_none() -> None:
    _install({"status": "failed", "grant": None})

    assert (
        parse_verification_grant(
            await check_verification_call("verif_001", code="000001")
        )
        is None
    )


@pytest.mark.unit
async def test_find_manageable_bookings_parses_candidates() -> None:
    seen = _install(
        {
            "status": "found",
            "candidates": [
                {
                    "booking_id": "bk_1",
                    "booking_reference": "CORD-1",
                    "business": "The Fitzroy",
                    "date": "2026-06-15",
                    "approx_time": "7:00 PM",
                    "party_size": 4,
                }
            ],
        }
    )

    candidates = parse_manageable_bookings(
        await find_manageable_bookings_call(phone_e164="+61477000111")
    )

    assert [c.booking_id for c in candidates] == ["bk_1"]
    assert seen[0].url.params["phone_e164"] == "+61477000111"


@pytest.mark.unit
async def test_no_candidates_parses_to_empty() -> None:
    _install({"status": "none", "candidates": []})

    assert (
        parse_manageable_bookings(
            await find_manageable_bookings_call(phone_e164="+61477000111")
        )
        == []
    )


@pytest.mark.unit
async def test_booking_details_carries_the_version_without_a_grant() -> None:
    """The version is the token a mutation must echo back (spec 08 §9.4.8)."""
    _install(
        {
            "status": "ok",
            "booking": {
                "booking_id": "bk_1",
                "booking_reference": "CORD-1",
                "status": "confirmed",
                "party_size": 4,
                "version": 7,
            },
        }
    )

    projection = parse_booking_projection(await get_booking_details_call("bk_1"))

    assert projection is not None
    assert projection.version == 7
