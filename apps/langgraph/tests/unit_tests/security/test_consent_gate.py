"""Unit tests for the spec-14 consent gate node (§5.1/§5.2, fail-closed §6)."""

from __future__ import annotations

import json

import httpx
import pytest
from langgraph.graph import END

from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.security.consent import (
    classify_consent_response,
    consent_gate,
    route_after_consent_gate,
)
from cordia_agent.state import AgentState, Channel, SourceInfo
from cordia_agent.tools.client import (
    ToolClient,
    reset_tool_client,
    set_tool_client,
)


_FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id="tenant_consent_test",
    channel=Channel.voice,
    channel_endpoint_id="ep_consent_001",
    configuration_version="v1",
    correlation_id="corr_consent_001",
    allowed_policy_ids=["policy_a"],
)

_POLICY_REQUIRED = {
    "jurisdiction_policy_id": "jp_us_ca",
    "consent_policy_version": "us-ca-two-party:2026-01",
    "notice_required": True,
    "notice_text": "This call is recorded. OK?",
}

_POLICY_NOT_REQUIRED = {
    "jurisdiction_policy_id": "jp_au_default",
    "consent_policy_version": "au-default:2026-01",
    "notice_required": False,
    "notice_text": None,
}


def _json_response(status: int, body: dict) -> httpx.Response:
    return httpx.Response(status, content=json.dumps(body).encode())


def _make_client(handler) -> ToolClient:
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    return ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)


def _state(**overrides: object) -> AgentState:
    kwargs: dict = {
        "tenant_id": "tenant_consent_test",
        "session_id": "sess_1",
        "source": SourceInfo(channel=Channel.voice, language="en-AU"),
    }
    kwargs.update(overrides)
    return AgentState(**kwargs)


@pytest.fixture(autouse=True)
def _reset_after_test():
    yield
    reset_tool_client()


def _handler(policy: dict, record_status: int = 200):
    """Fake API serving the consent policy + record endpoints; counts calls."""
    calls = {"record": 0, "record_bodies": [], "policy": 0}

    def handle(req: httpx.Request) -> httpx.Response:
        if req.url.path == "/api/consent-policies":
            calls["policy"] += 1
            return _json_response(200, policy)
        if req.url.path.endswith("/consent"):
            calls["record"] += 1
            calls["record_bodies"].append(json.loads(req.content))
            if record_status != 200:
                return _json_response(record_status, {"status": "error"})
            body = json.loads(req.content)
            return _json_response(
                200,
                {
                    "status": "recorded",
                    "session_id": "sess_1",
                    "recording_allowed": body.get("recording_allowed", False),
                },
            )
        return _json_response(404, {"status": "not_found"})

    return handle, calls


@pytest.mark.unit
async def test_not_required_records_and_allows_recording() -> None:
    handle, calls = _handler(_POLICY_NOT_REQUIRED)
    set_tool_client(_make_client(handle))
    update = await consent_gate(_state())
    assert update["recording_allowed"] is True
    assert update["consent_state"]["outcome"] == "not_required"
    assert update["consent_state"]["jurisdiction_policy_id"] == "jp_au_default"
    assert calls["record"] == 1
    merged = _state(**update)
    assert route_after_consent_gate(merged) == "load_context"


@pytest.mark.unit
async def test_notice_required_emits_notice_stashes_policy_and_awaits() -> None:
    handle, calls = _handler(_POLICY_REQUIRED)
    set_tool_client(_make_client(handle))
    update = await consent_gate(_state())
    assert update["pending_consent_notice"] == "This call is recorded. OK?"
    assert update["recording_allowed"] is False
    assert update["consent_policy"]["jurisdiction_policy_id"] == "jp_us_ca"
    assert "consent_state" not in update
    assert calls["record"] == 0, "must not record before the customer replies"
    merged = _state(**update)
    assert route_after_consent_gate(merged) == END


@pytest.mark.unit
async def test_granted_reply_records_with_stashed_policy_no_second_resolve() -> None:
    handle, calls = _handler(_POLICY_REQUIRED)
    set_tool_client(_make_client(handle))
    state = _state(
        pending_consent_notice="This call is recorded. OK?",
        consent_policy=_POLICY_REQUIRED,
        transcript_text="yes that's fine",
    )
    update = await consent_gate(state)
    assert update["recording_allowed"] is True
    assert update["consent_state"]["outcome"] == "granted"
    assert (
        update["consent_state"]["consent_policy_version"] == "us-ca-two-party:2026-01"
    )
    assert update["pending_consent_notice"] is None
    assert calls["record"] == 1
    assert calls["policy"] == 0, (
        "the stashed policy must be reused — no second round trip"
    )
    assert calls["record_bodies"][0]["recording_allowed"] is True
    assert route_after_consent_gate(_state(**update)) == "load_context"


@pytest.mark.unit
async def test_declined_reply_records_alternative_and_suppresses() -> None:
    handle, calls = _handler(_POLICY_REQUIRED)
    set_tool_client(_make_client(handle))
    state = _state(
        pending_consent_notice="This call is recorded. OK?",
        consent_policy=_POLICY_REQUIRED,
        transcript_text="no, please don't record me",
    )
    update = await consent_gate(state)
    assert update["recording_allowed"] is False
    assert update["consent_state"]["outcome"] == "alternative_offered"
    assert calls["record_bodies"][0]["recording_allowed"] is False
    # Non-recorded session still continues (§5.2 Active_NonRecorded).
    assert route_after_consent_gate(_state(**update)) == "load_context"


@pytest.mark.unit
async def test_unparseable_reply_keeps_awaiting() -> None:
    handle, calls = _handler(_POLICY_REQUIRED)
    set_tool_client(_make_client(handle))
    state = _state(
        pending_consent_notice="This call is recorded. OK?",
        transcript_text="table for four on friday",
    )
    update = await consent_gate(state)
    assert update == {"recording_allowed": False}
    assert calls["record"] == 0
    merged = state.model_copy(update=update)
    assert route_after_consent_gate(merged) == END


@pytest.mark.unit
async def test_policy_resolution_failure_fails_closed_non_recorded() -> None:
    def handle(req: httpx.Request) -> httpx.Response:
        return _json_response(500, {"status": "error"})

    set_tool_client(_make_client(handle))
    update = await consent_gate(_state())
    assert update == {"recording_allowed": False}
    merged = _state(**update)
    # Continue NON-recorded; consent retried next turn (consent_state is None).
    assert route_after_consent_gate(merged) == "load_context"
    assert merged.consent_state is None


@pytest.mark.unit
async def test_record_failure_fails_closed_and_retries_next_turn() -> None:
    handle, calls = _handler(_POLICY_NOT_REQUIRED, record_status=500)
    set_tool_client(_make_client(handle))
    update = await consent_gate(_state())
    assert update["recording_allowed"] is False
    assert "consent_state" not in update
    assert calls["record"] == 1


@pytest.mark.unit
async def test_record_failure_after_grant_never_reasks_the_customer() -> None:
    """A persist failure must not discard the customer's reply (§8 fail table)."""
    handle, calls = _handler(_POLICY_REQUIRED, record_status=500)
    set_tool_client(_make_client(handle))
    state = _state(
        pending_consent_notice="This call is recorded. OK?",
        consent_policy=_POLICY_REQUIRED,
        transcript_text="yes go ahead",
    )
    update = await consent_gate(state)
    # Reply stashed; notice NOT re-emitted; conversation continues non-recorded.
    assert update["consent_reply"] == "granted"
    assert update["pending_consent_notice"] is None
    assert update["recording_allowed"] is False
    merged = state.model_copy(update=update)
    assert route_after_consent_gate(merged) == "load_context"

    # Next turn: the API recovers; the stashed reply is persisted directly —
    # no notice, no re-classification of the (unrelated) new transcript.
    handle2, calls2 = _handler(_POLICY_REQUIRED)
    set_tool_client(_make_client(handle2))
    retry_state = merged.model_copy(
        update={"transcript_text": "table for four on friday"}
    )
    retry_update = await consent_gate(retry_state)
    assert retry_update["consent_state"]["outcome"] == "granted"
    assert retry_update["recording_allowed"] is True
    assert retry_update["consent_reply"] is None
    assert calls2["record"] == 1


@pytest.mark.unit
async def test_already_recorded_is_idempotent_passthrough() -> None:
    def handle(req: httpx.Request) -> httpx.Response:  # pragma: no cover
        raise AssertionError("no API call expected once consent is recorded")

    set_tool_client(_make_client(handle))
    state = _state(
        consent_state={"outcome": "granted"},
        recording_allowed=True,
    )
    update = await consent_gate(state)
    assert update == {}
    assert route_after_consent_gate(state) == "load_context"


@pytest.mark.unit
def test_classifier_decline_wins_and_no_problem_is_grant() -> None:
    assert classify_consent_response("no problem, go ahead") == "granted"
    assert classify_consent_response("no, don't record, but okay") == "declined"
    assert classify_consent_response("yes please") == "granted"
    assert classify_consent_response("hmm what") is None


@pytest.mark.unit
def test_classifier_uncertainty_never_reads_as_consent() -> None:
    """'not sure' / 'maybe' must fail closed to None, never granted (§6)."""
    assert classify_consent_response("I'm not sure") is None
    assert classify_consent_response("maybe, what does that mean?") is None


@pytest.mark.unit
async def test_fresh_decline_overrides_stashed_grant_retry() -> None:
    """A later explicit decline must beat a stale stashed grant (fail-closed)."""
    handle, calls = _handler(_POLICY_REQUIRED)
    set_tool_client(_make_client(handle))
    state = _state(
        consent_reply="granted",  # stashed from a failed persist last turn
        consent_policy=_POLICY_REQUIRED,
        transcript_text="actually no, stop recording please",
    )
    update = await consent_gate(state)
    assert update["consent_state"]["outcome"] == "alternative_offered"
    assert update["recording_allowed"] is False
    assert calls["record_bodies"][0]["recording_allowed"] is False
