"""Unit tests for deriving the sitting from the time the caller asked for.

The property under test is what it REFUSES to derive. Picking a sitting by coin
toss books a real table against the wrong covers, so ambiguity has to leave the
field unset and let clarify ask.

The three refusals are not one refusal. "Nothing is served then", "several
sittings cover it" and "the probe did not answer" were all reported as ``None``,
so a caller asking for 3am and a caller talking to a broken system got the same
next question.
"""

from __future__ import annotations

import json
from datetime import UTC, datetime

import httpx
import pytest

from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.slots.sitting import SittingOutcome, derive_sitting
from cordia_agent.state import (
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
)
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client


_TENANT_CONTEXT = TenantContext(
    tenant_id="tenant_1",
    channel=Channel.voice,
    channel_endpoint_id="ep_1",
    configuration_version="v1",
    correlation_id="corr_1",
    session_id="sess_1",
    allowed_agent_policy_ids=["p"],
    allowed_tool_policy_ids=["p"],
    issued_at="2026-07-25T00:00:00Z",
    expires_at="2026-07-25T01:00:00Z",
)

_SERVICES = [
    {"resource_id": "breakfast", "kind": "service", "name": "Breakfast", "aliases": []},
    {"resource_id": "lunch", "kind": "service", "name": "Lunch sitting", "aliases": []},
    {
        "resource_id": "dinner",
        "kind": "service",
        "name": "Dinner sitting",
        "aliases": [],
    },
]


@pytest.fixture(autouse=True)
def _reset() -> None:
    yield
    reset_tool_client()


def install(*, tracked: set[str], services: list[dict] | None = None) -> None:
    """Serve the declared services, reporting tracked capacity only for *tracked*."""

    def handle(request: httpx.Request) -> httpx.Response:
        if "tenant-resources" in request.url.path:
            return httpx.Response(
                200,
                content=json.dumps(
                    {
                        "status": "listed",
                        "matches": _SERVICES if services is None else services,
                        "resolved": None,
                    }
                ).encode(),
            )
        body = json.loads(request.content.decode())
        service_id = body["draft"]["request"]["service_id"]
        # `informational` is what the API answers when NO capacity row matched —
        # the exact response a wrong sitting produces.
        mode = "live" if service_id in tracked else "informational"
        return httpx.Response(
            200,
            content=json.dumps({"mode": mode, "candidates": [], "hold": None}).encode(),
        )

    set_tool_client(
        ToolClient(
            httpx.AsyncClient(
                transport=httpx.MockTransport(handle), base_url="http://test"
            ),
            tenant_context=_TENANT_CONTEXT,
        )
    )


def _draft() -> BookingDraft:
    return BookingDraft(
        tenant_id="tenant_1",
        session_id="sess_1",
        schema_version="1.0",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.create,
            starts_at=datetime(2026, 9, 14, 9, 0, tzinfo=UTC),
            party_size=4,
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


@pytest.mark.unit
async def test_seven_pm_is_the_dinner_sitting() -> None:
    """Nobody rings a restaurant asking for "the dinner sitting"."""
    install(tracked={"dinner"})

    result = await derive_sitting(_draft())

    assert result.outcome is SittingOutcome.derived
    assert result.capture is not None
    assert result.capture.value == "dinner"


@pytest.mark.unit
async def test_the_derived_sitting_carries_its_name_for_read_back() -> None:
    install(tracked={"dinner"})

    result = await derive_sitting(_draft())

    assert result.capture is not None
    assert result.capture.resolved_name == "Dinner sitting"


@pytest.mark.unit
async def test_overlapping_sittings_are_a_question_not_a_coin_toss() -> None:
    """Two sittings with capacity at the same time is genuinely ambiguous, and
    guessing books against the wrong covers."""
    install(tracked={"lunch", "dinner"})

    result = await derive_sitting(_draft())

    assert result.outcome is SittingOutcome.ambiguous
    assert result.capture is None


@pytest.mark.unit
async def test_a_time_with_no_inventory_derives_nothing() -> None:
    """`informational` means no capacity row matched — the venue is not open
    then, which is not a sitting to pick."""
    install(tracked=set())

    result = await derive_sitting(_draft())

    assert result.outcome is SittingOutcome.outside_service
    assert result.capture is None
    # The names come back so the caller can be offered them by name rather than
    # asked to guess at a vocabulary only the venue knows.
    assert result.service_names == ("Breakfast", "Lunch sitting", "Dinner sitting")


@pytest.mark.unit
async def test_no_declared_services_derives_nothing() -> None:
    install(tracked={"dinner"}, services=[])

    result = await derive_sitting(_draft())

    assert result.outcome is SittingOutcome.unavailable
    assert result.service_names == ()


@pytest.mark.unit
async def test_a_failed_probe_is_not_a_closed_venue() -> None:
    """An unreachable availability service must not be reported as "not serving".

    Folding the failure into "no tracked capacity" had the agent tell callers the
    kitchen was shut whenever it had merely failed to ask.
    """

    def handle(request: httpx.Request) -> httpx.Response:
        if "tenant-resources" in request.url.path:
            return httpx.Response(
                200,
                content=json.dumps(
                    {"status": "listed", "matches": _SERVICES, "resolved": None}
                ).encode(),
            )
        return httpx.Response(503, content=b"{}")

    set_tool_client(
        ToolClient(
            httpx.AsyncClient(
                transport=httpx.MockTransport(handle), base_url="http://test"
            ),
            tenant_context=_TENANT_CONTEXT,
        )
    )

    result = await derive_sitting(_draft())

    assert result.outcome is SittingOutcome.unavailable
