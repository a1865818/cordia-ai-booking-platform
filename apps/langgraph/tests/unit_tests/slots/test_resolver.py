"""Unit tests for turning a spoken name into a declared identifier.

The property under test is not "does it resolve" but "what does it refuse".
The extractor is forbidden from inventing identifiers because a fabricated
``service_id`` is submitted verbatim and, with the validation stubs still in DI,
nothing downstream refuses it. This step is the only place a name becomes an id,
so it has to be the place that cannot be talked into inventing one.
"""

from __future__ import annotations

import json

import httpx
import pytest

from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.slots.capture import SlotCapture
from cordia_agent.slots.resolver import resolve_resource_names
from cordia_agent.state import CaptureMethod, Channel
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

#: What the tenant has actually declared, keyed by the query the agent sends.
DECLARED: dict[str, dict] = {
    "dinner": {
        "status": "resolved",
        "matches": [{"resource_id": "dinner", "kind": "service", "name": "Dinner sitting", "aliases": []}],
        "resolved": {"resource_id": "dinner", "kind": "service", "name": "Dinner sitting", "aliases": []},
    },
    "the fitzroy room": {
        "status": "resolved",
        "matches": [{"resource_id": "fitzroy", "kind": "location", "name": "Fitzroy dining room", "aliases": []}],
        "resolved": {"resource_id": "fitzroy", "kind": "location", "name": "Fitzroy dining room", "aliases": []},
    },
    "sitting": {
        "status": "ambiguous",
        "matches": [
            {"resource_id": "lunch", "kind": "service", "name": "Lunch sitting", "aliases": []},
            {"resource_id": "dinner", "kind": "service", "name": "Dinner sitting", "aliases": []},
        ],
        "resolved": None,
    },
}

NO_MATCH = {"status": "no_match", "matches": [], "resolved": None}


@pytest.fixture(autouse=True)
def _reset() -> None:
    yield
    reset_tool_client()


def install_client(*, status: int = 200, fail: bool = False) -> list[dict[str, str]]:
    """Install a ToolClient serving DECLARED, recording every query it saw."""
    seen: list[dict[str, str]] = []

    def handle(request: httpx.Request) -> httpx.Response:
        seen.append(dict(request.url.params))
        if fail:
            return httpx.Response(503, content=b'{"status":"unavailable"}')
        query = (request.url.params.get("query") or "").lower()
        body = DECLARED.get(query, NO_MATCH)
        return httpx.Response(status, content=json.dumps(body).encode())

    set_tool_client(
        ToolClient(
            httpx.AsyncClient(transport=httpx.MockTransport(handle), base_url="http://test"),
            tenant_context=_TENANT_CONTEXT,
        )
    )
    return seen


def name(field: str, value: str) -> SlotCapture:
    return SlotCapture(
        field=field,
        value=value,
        capture_method=CaptureMethod.speech,
        model_confidence=0.9,
        normalized_value=value,
    )


def fields(captures: list[SlotCapture]) -> dict[str, object]:
    return {c.field: c.value for c in captures}


@pytest.mark.unit
async def test_a_declared_name_becomes_its_identifier() -> None:
    install_client()

    out = await resolve_resource_names([name("service_name", "dinner")])

    assert fields(out) == {"service_id": "dinner"}


@pytest.mark.unit
async def test_an_alias_phrase_resolves_too() -> None:
    install_client()

    out = await resolve_resource_names([name("location_name", "the Fitzroy room")])

    assert fields(out) == {"location_id": "fitzroy"}


@pytest.mark.unit
async def test_an_undeclared_name_captures_nothing() -> None:
    """"a haircut" must not become a service_id. The draft stays incomplete and
    clarify asks — which is the entire point of resolving rather than guessing."""
    install_client()

    out = await resolve_resource_names([name("service_name", "a haircut")])

    assert out == []


@pytest.mark.unit
async def test_an_ambiguous_name_captures_nothing() -> None:
    """Two candidates is a question for the customer, never a coin toss."""
    install_client()

    out = await resolve_resource_names([name("service_name", "sitting")])

    assert out == []


@pytest.mark.unit
async def test_a_failing_lookup_captures_nothing() -> None:
    """A tool outage must not fall back to trusting the spoken name."""
    install_client(fail=True)

    out = await resolve_resource_names([name("service_name", "dinner")])

    assert out == []


@pytest.mark.unit
async def test_an_explicitly_spoken_identifier_wins() -> None:
    """The customer said 'service svc_001' outright; do not re-resolve a name."""
    seen = install_client()

    out = await resolve_resource_names(
        [name("service_id", "svc_001"), name("service_name", "dinner")]
    )

    assert fields(out) == {"service_id": "svc_001"}
    assert seen == [], "no lookup should happen when the id was spoken"


@pytest.mark.unit
async def test_other_captures_pass_through_untouched() -> None:
    install_client()

    out = await resolve_resource_names(
        [name("service_name", "dinner"), name("party_size", "4")]
    )

    assert fields(out) == {"party_size": "4", "service_id": "dinner"}


@pytest.mark.unit
async def test_nothing_to_resolve_short_circuits() -> None:
    """A turn with no spoken names must not cost a round trip."""
    seen = install_client()

    captures = [name("party_size", "4")]
    assert await resolve_resource_names(captures) is captures
    assert seen == []


@pytest.mark.unit
async def test_each_kind_is_looked_up_as_its_own_kind() -> None:
    """A room must never resolve against the service list, or vice versa."""
    seen = install_client()

    await resolve_resource_names(
        [name("service_name", "dinner"), name("location_name", "the Fitzroy room")]
    )

    assert {s["kind"] for s in seen} == {"service", "location"}


@pytest.mark.unit
async def test_provenance_survives_resolution() -> None:
    """The resolved capture is still the customer's utterance, so how it was
    captured and how confident the model was must carry over."""
    install_client()

    out = await resolve_resource_names([name("service_name", "dinner")])

    assert out[0].capture_method == CaptureMethod.speech
    assert out[0].model_confidence == 0.9
