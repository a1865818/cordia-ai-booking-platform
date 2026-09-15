"""Unit tests for populating ``disclosures_presented`` from production nodes.

``await_confirmation`` refuses to record a ``Confirmation`` while the field is
empty (§5.5/§14), and until now nothing in production ever wrote to it — the
text runner patched it in through ``aupdate_state``.  A phone call has no such
driver, so the gate was a hard stop on voice.  These tests pin that the two
nodes which genuinely present a disclosure now say so, and that no other node
claims one.
"""

from __future__ import annotations

import json
from datetime import UTC, datetime

import httpx
import pytest

from cordia_agent.models.disclosures import (
    AVAILABILITY_ADVISORY,
    BOOKING_TERMS,
    RECORDING_NOTICE,
    merge_disclosures,
)
from cordia_agent.nodes.await_confirmation import await_confirmation, consent_satisfied
from cordia_agent.nodes.read_back import read_back
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.security.consent import consent_gate
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
)
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from cordia_agent.tools.status_codes import AvailabilityMode


# ---------------------------------------------------------------------------
# merge_disclosures
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_merge_appends_without_duplicating() -> None:
    """A node that runs twice has not disclosed twice."""
    assert merge_disclosures(["a"], "b", "a") == ["a", "b"]


@pytest.mark.unit
def test_merge_does_not_mutate_the_input() -> None:
    existing = ["a"]
    merge_disclosures(existing, "b")
    assert existing == ["a"]


# ---------------------------------------------------------------------------
# read_back
# ---------------------------------------------------------------------------


def _draft() -> BookingDraft:
    return BookingDraft(
        tenant_id="tenant_1",
        session_id="sess_1",
        schema_version="v1",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.create,
            service_id="svc_1",
            location_id="loc_1",
            starts_at=datetime(2026, 8, 10, 19, 0, tzinfo=UTC),
            party_size=4,
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


@pytest.mark.unit
async def test_read_back_records_the_terms_it_just_presented() -> None:
    """The read-back IS a disclosure — it reads the material terms aloud."""
    state = AgentState(draft=_draft(), availability_mode=AvailabilityMode.HELD)
    update = await read_back(state)

    assert BOOKING_TERMS in update["disclosures_presented"]


@pytest.mark.unit
async def test_informational_availability_adds_the_inv16_caveat() -> None:
    """An unheld slot must be disclosed as advisory, not presented as a guarantee."""
    state = AgentState(draft=_draft(), availability_mode=AvailabilityMode.INFORMATIONAL)
    update = await read_back(state)

    assert AVAILABILITY_ADVISORY in update["disclosures_presented"]


@pytest.mark.unit
async def test_held_availability_does_not_claim_the_advisory_caveat() -> None:
    state = AgentState(draft=_draft(), availability_mode=AvailabilityMode.HELD)
    update = await read_back(state)

    assert AVAILABILITY_ADVISORY not in update["disclosures_presented"]


@pytest.mark.unit
async def test_read_back_preserves_disclosures_from_earlier_nodes() -> None:
    """The consent gate ran first; its notice must survive the read-back."""
    state = AgentState(
        draft=_draft(),
        availability_mode=AvailabilityMode.HELD,
        disclosures_presented=[RECORDING_NOTICE],
    )
    update = await read_back(state)

    assert update["disclosures_presented"] == [RECORDING_NOTICE, BOOKING_TERMS]


@pytest.mark.unit
async def test_read_back_with_no_draft_claims_nothing() -> None:
    assert await read_back(AgentState()) == {}


# ---------------------------------------------------------------------------
# consent_gate
# ---------------------------------------------------------------------------

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

_NOTICE_REQUIRED = {
    "jurisdiction_policy_id": "jp_us_ca",
    "consent_policy_version": "us-ca:2026-01",
    "notice_required": True,
    "notice_text": "This call is recorded. OK?",
}

_NOTICE_NOT_REQUIRED = {
    "jurisdiction_policy_id": "jp_au",
    "consent_policy_version": "au:2026-01",
    "notice_required": False,
    "notice_text": None,
}


def _client(policy: dict, *, record_status: int = 200) -> ToolClient:
    def handle(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/api/consent-policies":
            return httpx.Response(200, content=json.dumps(policy).encode())
        if request.url.path.endswith("/consent"):
            if record_status != 200:
                return httpx.Response(record_status, content=b'{"status":"error"}')
            return httpx.Response(
                200,
                content=json.dumps(
                    {
                        "status": "recorded",
                        "session_id": "sess_1",
                        "recording_allowed": True,
                    }
                ).encode(),
            )
        return httpx.Response(404, content=b'{"status":"not_found"}')

    return ToolClient(
        httpx.AsyncClient(
            transport=httpx.MockTransport(handle), base_url="http://test"
        ),
        tenant_context=_TENANT_CONTEXT,
    )


@pytest.fixture(autouse=True)
def _reset_tool_client():
    yield
    reset_tool_client()


def _awaiting_reply(**overrides: object) -> AgentState:
    kwargs: dict = {
        "tenant_id": "tenant_1",
        "session_id": "sess_1",
        "source": SourceInfo(channel=Channel.voice, language="en-AU"),
        "pending_consent_notice": "This call is recorded. OK?",
        "consent_policy": _NOTICE_REQUIRED,
        "transcript_text": "yes, that's fine",
    }
    kwargs.update(overrides)
    return AgentState(**kwargs)


@pytest.mark.unit
async def test_recording_notice_is_recorded_once_the_reply_lands() -> None:
    """By the time a reply is recorded, the notice has definitely been played."""
    set_tool_client(_client(_NOTICE_REQUIRED))
    update = await consent_gate(_awaiting_reply())

    assert update["disclosures_presented"] == [RECORDING_NOTICE]


@pytest.mark.unit
async def test_a_failed_persist_does_not_un_play_the_notice() -> None:
    """The customer heard it; a database failure does not change that fact."""
    set_tool_client(_client(_NOTICE_REQUIRED, record_status=500))
    update = await consent_gate(_awaiting_reply())

    assert update["disclosures_presented"] == [RECORDING_NOTICE]
    assert update["recording_allowed"] is False


@pytest.mark.unit
async def test_a_policy_needing_no_notice_claims_no_disclosure() -> None:
    """Nothing was shown, so nothing may be asserted."""
    set_tool_client(_client(_NOTICE_NOT_REQUIRED))
    update = await consent_gate(
        AgentState(
            tenant_id="tenant_1",
            session_id="sess_1",
            source=SourceInfo(channel=Channel.voice, language="en-AU"),
        )
    )

    assert "disclosures_presented" not in update


@pytest.mark.unit
async def test_the_first_pass_claims_nothing_until_the_customer_answers() -> None:
    """Emitting the notice is not the same as having presented it to a listener."""
    set_tool_client(_client(_NOTICE_REQUIRED))
    update = await consent_gate(
        AgentState(
            tenant_id="tenant_1",
            session_id="sess_1",
            source=SourceInfo(channel=Channel.voice, language="en-AU"),
        )
    )

    assert update["pending_consent_notice"]
    assert "disclosures_presented" not in update


# ---------------------------------------------------------------------------
# The gate this all exists to satisfy
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_consent_gate_plus_read_back_satisfy_the_confirmation_gate() -> None:
    """End of the story: production nodes alone can now get past §5.5.

    Previously only a driver calling ``aupdate_state`` could, which is why
    voice could not book at all.
    """
    set_tool_client(_client(_NOTICE_REQUIRED))
    after_consent = await consent_gate(_awaiting_reply())

    state = AgentState(
        draft=_draft(),
        availability_mode=AvailabilityMode.INFORMATIONAL,
        disclosures_presented=after_consent["disclosures_presented"],
    )
    after_read_back = await read_back(state)

    assert consent_satisfied(after_read_back["disclosures_presented"]) is True

    confirmed = await await_confirmation(
        AgentState(
            draft=_draft(),
            transcript_text="yes, that's right",
            transcript_is_final=True,
            read_back_version=after_read_back["read_back_version"],
            current_revision=after_read_back["current_revision"],
            current_turn_id="turn_1",
            disclosures_presented=after_read_back["disclosures_presented"],
        )
    )

    assert confirmed["confirmation"] is not None, (
        "the disclosures recorded by production nodes must satisfy the §5.5 gate"
    )
    assert set(confirmed["confirmation"]["disclosures_presented"]) == {
        RECORDING_NOTICE,
        BOOKING_TERMS,
        AVAILABILITY_ADVISORY,
    }


@pytest.mark.unit
async def test_an_unchanged_read_back_is_not_said_twice() -> None:
    """read_back re-renders on the turn the customer confirms on, so the whole
    booking was read out again immediately before the acknowledgement:
    "...shall I book it in for you? Got it, that's gone through." Nobody reads a
    booking back to themselves and then says "got it"."""
    draft = _draft()
    first = await read_back(
        AgentState(draft=draft, availability_mode=AvailabilityMode.HELD)
    )

    assert "read_back_text" in first

    heard = AgentState(
        draft=draft,
        availability_mode=AvailabilityMode.HELD,
        read_back_version=first["read_back_version"],
        read_back_first_presented_turn_id="turn_001",
        current_turn_id="turn_002",
    )
    second = await read_back(heard)

    assert "read_back_text" not in second
    assert second["read_back_version"] == first["read_back_version"]


@pytest.mark.unit
async def test_a_changed_read_back_is_said_again() -> None:
    """Suppression is keyed on the version, so a customer who changes something
    hears the new terms — which is the whole point of reading them back."""
    first = await read_back(
        AgentState(draft=_draft(), availability_mode=AvailabilityMode.HELD)
    )

    changed = _draft()
    changed = changed.model_copy(
        update={"request": changed.request.model_copy(update={"party_size": 6})}
    )
    second = await read_back(
        AgentState(
            draft=changed,
            availability_mode=AvailabilityMode.HELD,
            read_back_version=first["read_back_version"],
            read_back_first_presented_turn_id="turn_001",
            current_turn_id="turn_002",
        )
    )

    assert "read_back_text" in second
    assert "six people" in second["read_back_text"]
