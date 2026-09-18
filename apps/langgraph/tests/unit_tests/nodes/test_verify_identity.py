"""Unit tests for verify_identity node and helpers (02-K, §9.3, Inv-11).

Covers:
  - Inv-11 valid grant advances: VALID grant → verification_grant_id set,
    verification_outcome "valid", route == "capture_fields".
  - Inv-11 forged/absent grant → handoff: grant_id=None (or wrong tenant /
    wrong action) → verification_outcome "invalid", route == "human_handoff";
    verification_grant_id NOT set; no submit/mutation call.
  - Inv-11 caller-id-alone never advances: check_verification returns
    failed/non-ok → route human_handoff, grant not set.
  - Expiry enforced: validate_grant with expires_at <= now returns EXPIRED;
    node leaves verification_grant_id unset and routes capture_fields (re-verify).
  - No operation_id minted/advanced: verify_identity issues no
    submit_confirmed_intent call (only check_verification).
"""

from __future__ import annotations

import datetime
import json
from typing import Any

import httpx
import pytest
from langgraph.graph import END

from cordia_agent.nodes.verify_identity import (
    MAX_VERIFICATION_ATTEMPTS,
    GrantOutcome,
    extract_verification_code,
    route_after_verify_identity,
    validate_grant,
    verify_identity,
)
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
)
from cordia_agent.streaming.agent_reply import REPLY_TEXT_KEYS, segment_for
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from cordia_agent.tools.contracts import VerificationGrantView
from cordia_agent.tools.status_codes import VerificationAction


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_TENANT_ID = "tenant_vi_test"
_VERIFICATION_ID = "verif_001"

_NOW = datetime.datetime(2026, 7, 1, 12, 0, 0, tzinfo=datetime.UTC)
_FUTURE = datetime.datetime(2026, 7, 1, 13, 0, 0, tzinfo=datetime.UTC)  # 1 h ahead
_PAST = datetime.datetime(2026, 7, 1, 11, 0, 0, tzinfo=datetime.UTC)  # 1 h ago

_FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id=_TENANT_ID,
    channel=Channel.voice,
    channel_endpoint_id="ep_vi_001",
    configuration_version="v1",
    correlation_id="corr_vi_001",
    session_id="sess_vi_001",
    allowed_agent_policy_ids=["policy_a"],
    allowed_tool_policy_ids=["policy_a"],
    issued_at="2026-07-25T00:00:00Z",
    expires_at="2026-07-25T01:00:00Z",
)


def _json_response(status_code: int, body: dict[str, Any]) -> httpx.Response:
    return httpx.Response(
        status_code=status_code,
        headers={"content-type": "application/json"},
        content=json.dumps(body).encode(),
    )


def _make_client(handler) -> ToolClient:
    transport = httpx.MockTransport(handler)
    http = httpx.AsyncClient(transport=transport, base_url="http://test")
    return ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT)


def _granted_body(
    *,
    grant_id: str | None = "grant_001",
    tenant_id: str = _TENANT_ID,
    allowed_action: str = "modify",
    expires_at: str | None = None,
) -> dict:
    """Build a 200 response body for check_verification (granted).

    The ToolClient wraps the entire 200 body as ``ToolResultEnvelope.data``.
    ``parse_verification_grant`` calls ``_payload(env.data, VerificationGrantView)``
    which strips envelope-only keys (``status``, ``ok``, ``correlation_id``, …)
    before model_validate — so grant fields must be at the TOP level of the body,
    not nested under a ``"data"`` key.

    ``expires_at`` defaults to a timestamp always in the future relative to real
    wall-clock ``now``: the node-level tests drive ``verify_identity`` which validates
    the grant against ``datetime.now(UTC)`` (not the injected ``_NOW``), so a hardcoded
    date would make the grant "expire" once that date passes. Tests that need a specific
    expiry (boundary / expired cases) pass ``expires_at`` explicitly.
    """
    if expires_at is None:
        expires_at = (
            datetime.datetime.now(datetime.UTC) + datetime.timedelta(days=1)
        ).isoformat()
    return {
        "status": "granted",
        "grant_id": grant_id,
        "tenant_id": tenant_id,
        "customer_id": "cust_001",
        "target_booking_id": None,
        "allowed_action": allowed_action,
        "expires_at": expires_at,
        "scope": "session",
    }


def _failed_body() -> dict:
    """Build a non-2xx response body for check_verification (failed/pending).

    We use a non-2xx status_code (400) so the ToolClient sets ok=False,
    which causes parse_verification_grant to return None (no grant).
    """
    return {
        "status": "failed",
    }


class RecordingHandler:
    """Fake handler that records all requests (to assert no submit calls)."""

    def __init__(
        self,
        verify_body: dict | None = None,
        verify_status: int = 200,
    ) -> None:
        self.requests: list[httpx.Request] = []
        self._verify_body = verify_body or _granted_body()
        self._verify_status = verify_status

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        path = request.url.path
        if "/api/verifications/" in path:
            return _json_response(self._verify_status, self._verify_body)
        if path == "/api/bookings":
            # submit_confirmed_intent — should NOT be called from verify_identity
            return _json_response(
                200,
                {
                    "status": "accepted",
                    "operation_id": "op_001",
                    "intent_id": "intent_001",
                    "intent_revision": 1,
                    "status_url": "/api/booking-operations/op_001",
                    "submitted_at": "2026-07-01T12:00:00+00:00",
                },
            )
        return _json_response(404, {"error": "not_found"})

    def submit_call_count(self) -> int:
        return sum(1 for r in self.requests if r.url.path == "/api/bookings")


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------


@pytest.fixture(autouse=True)
def _reset_after_test():
    yield
    reset_tool_client()


# ---------------------------------------------------------------------------
# validate_grant unit tests (pure function — no tool client needed)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_validate_grant_valid() -> None:
    """VALID: in-scope, unexpired grant → VALID outcome."""
    grant = VerificationGrantView(
        grant_id="grant_001",
        tenant_id=_TENANT_ID,
        allowed_action=VerificationAction.MODIFY,
        expires_at=_FUTURE,
        scope="session",
    )
    outcome = validate_grant(
        grant,
        tenant_id=_TENANT_ID,
        action=VerificationAction.MODIFY,
        now=_NOW,
    )
    assert outcome is GrantOutcome.VALID


@pytest.mark.unit
def test_validate_grant_none_is_invalid() -> None:
    """INVALID: None grant → INVALID (no platform grant, Inv-11)."""
    outcome = validate_grant(
        None,
        tenant_id=_TENANT_ID,
        action=VerificationAction.MODIFY,
        now=_NOW,
    )
    assert outcome is GrantOutcome.INVALID


@pytest.mark.unit
def test_validate_grant_no_grant_id_is_invalid() -> None:
    """INVALID: grant_id=None → INVALID (caller ID alone, Inv-11)."""
    grant = VerificationGrantView(
        grant_id=None,
        tenant_id=_TENANT_ID,
        allowed_action=VerificationAction.MODIFY,
        expires_at=_FUTURE,
        scope="session",
    )
    outcome = validate_grant(
        grant,
        tenant_id=_TENANT_ID,
        action=VerificationAction.MODIFY,
        now=_NOW,
    )
    assert outcome is GrantOutcome.INVALID


@pytest.mark.unit
def test_validate_grant_wrong_tenant_is_invalid() -> None:
    """INVALID: grant tenant_id != session tenant_id → INVALID (forged, Inv-11)."""
    grant = VerificationGrantView(
        grant_id="grant_001",
        tenant_id="tenant_other",
        allowed_action=VerificationAction.MODIFY,
        expires_at=_FUTURE,
        scope="session",
    )
    outcome = validate_grant(
        grant,
        tenant_id=_TENANT_ID,
        action=VerificationAction.MODIFY,
        now=_NOW,
    )
    assert outcome is GrantOutcome.INVALID


@pytest.mark.unit
def test_validate_grant_wrong_action_is_invalid() -> None:
    """INVALID: grant allowed_action != required action → INVALID (wrong scope, Inv-11)."""
    grant = VerificationGrantView(
        grant_id="grant_001",
        tenant_id=_TENANT_ID,
        allowed_action=VerificationAction.CANCEL,  # required is MODIFY
        expires_at=_FUTURE,
        scope="session",
    )
    outcome = validate_grant(
        grant,
        tenant_id=_TENANT_ID,
        action=VerificationAction.MODIFY,
        now=_NOW,
    )
    assert outcome is GrantOutcome.INVALID


@pytest.mark.unit
def test_validate_grant_expired_is_expired() -> None:
    """EXPIRED: expires_at <= now → EXPIRED (not VALID, not INVALID)."""
    # Exact boundary: expires_at == now → expired
    grant = VerificationGrantView(
        grant_id="grant_001",
        tenant_id=_TENANT_ID,
        allowed_action=VerificationAction.MODIFY,
        expires_at=_NOW,  # exactly equal → expired
        scope="session",
    )
    outcome = validate_grant(
        grant,
        tenant_id=_TENANT_ID,
        action=VerificationAction.MODIFY,
        now=_NOW,
    )
    assert outcome is GrantOutcome.EXPIRED


@pytest.mark.unit
def test_validate_grant_past_expiry_is_expired() -> None:
    """EXPIRED: expires_at < now → EXPIRED."""
    grant = VerificationGrantView(
        grant_id="grant_001",
        tenant_id=_TENANT_ID,
        allowed_action=VerificationAction.MODIFY,
        expires_at=_PAST,
        scope="session",
    )
    outcome = validate_grant(
        grant,
        tenant_id=_TENANT_ID,
        action=VerificationAction.MODIFY,
        now=_NOW,
    )
    assert outcome is GrantOutcome.EXPIRED


# ---------------------------------------------------------------------------
# route_after_verify_identity unit tests (pure function)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_route_valid_outcome_is_capture_fields() -> None:
    """VALID outcome → route 'capture_fields' (advance)."""
    state = AgentState(verification_outcome="valid")
    assert route_after_verify_identity(state) == "capture_fields"


@pytest.mark.unit
def test_route_expired_outcome_ends_the_turn() -> None:
    """EXPIRED outcome → END: a fresh code can only arrive on a later turn.

    Continuing to capture_fields sent the same mutation draft straight back to
    verify_identity with the grant still cleared — a cycle, not a retry.
    """
    state = AgentState(verification_outcome="expired", verification_grant_id=None)
    assert route_after_verify_identity(state) == END


@pytest.mark.unit
def test_route_invalid_outcome_is_human_handoff() -> None:
    """INVALID outcome → route 'human_handoff' (Inv-11)."""
    state = AgentState(verification_outcome="invalid", terminal_state="human_handoff")
    assert route_after_verify_identity(state) == "human_handoff"


@pytest.mark.unit
def test_route_terminal_state_human_handoff_overrides() -> None:
    """terminal_state='human_handoff' → route 'human_handoff' regardless of outcome."""
    state = AgentState(terminal_state="human_handoff", verification_outcome=None)
    assert route_after_verify_identity(state) == "human_handoff"


# ---------------------------------------------------------------------------
# verify_identity node integration tests (with fake tool client)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_inv11_valid_grant_advances() -> None:
    """Inv-11 valid grant advances: valid in-scope unexpired grant → grant_id set,
    outcome 'valid', route 'capture_fields'."""
    handler = RecordingHandler(verify_body=_granted_body())
    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id="sess_vi_001",
        operation=OperationType.modify,
        verification_id=_VERIFICATION_ID,
    )

    result = await verify_identity(state)

    assert result["verification_grant_id"] == "grant_001", (
        "Inv-11: valid grant must set verification_grant_id"
    )
    assert result["verification_outcome"] == "valid"
    assert "operation_id" not in result or result.get("operation_id") is None, (
        "verify_identity must NOT mint/advance operation_id"
    )

    # Assert NO submit_confirmed_intent call originated (only check_verification)
    assert handler.submit_call_count() == 0, (
        "verify_identity must NOT issue any submit call"
    )

    # Router check
    updated_state = AgentState(**{**state.model_dump(), **result})
    assert route_after_verify_identity(updated_state) == "capture_fields"


@pytest.mark.unit
async def test_inv11_absent_grant_id_routes_handoff() -> None:
    """Inv-11 absent grant (grant_id=None) → outcome 'invalid', route 'human_handoff';
    verification_grant_id NOT set; no submit call."""
    handler = RecordingHandler(
        verify_body=_granted_body(grant_id=None)  # no platform grant
    )
    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id="sess_vi_002",
        operation=OperationType.modify,
        verification_id=_VERIFICATION_ID,
    )

    result = await verify_identity(state)

    assert result["verification_outcome"] == "invalid"
    assert "verification_grant_id" not in result, (
        "Inv-11: no grant must NOT set verification_grant_id"
    )
    assert result.get("terminal_state") == "human_handoff"
    assert handler.submit_call_count() == 0

    updated_state = AgentState(**{**state.model_dump(), **result})
    assert route_after_verify_identity(updated_state) == "human_handoff"


@pytest.mark.unit
async def test_inv11_wrong_tenant_routes_handoff() -> None:
    """Inv-11 forged grant (wrong tenant) → outcome 'invalid', route 'human_handoff'."""
    handler = RecordingHandler(verify_body=_granted_body(tenant_id="tenant_evil"))
    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id="sess_vi_003",
        operation=OperationType.modify,
        verification_id=_VERIFICATION_ID,
    )

    result = await verify_identity(state)

    assert result["verification_outcome"] == "invalid"
    assert result.get("terminal_state") == "human_handoff"
    assert "verification_grant_id" not in result
    assert handler.submit_call_count() == 0

    updated_state = AgentState(**{**state.model_dump(), **result})
    assert route_after_verify_identity(updated_state) == "human_handoff"


@pytest.mark.unit
async def test_inv11_wrong_action_routes_handoff() -> None:
    """Inv-11 wrong action grant → outcome 'invalid', route 'human_handoff'."""
    handler = RecordingHandler(
        verify_body=_granted_body(allowed_action="cancel")  # required is modify
    )
    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id="sess_vi_004",
        operation=OperationType.modify,
        verification_id=_VERIFICATION_ID,
    )

    result = await verify_identity(state)

    assert result["verification_outcome"] == "invalid"
    assert result.get("terminal_state") == "human_handoff"
    assert handler.submit_call_count() == 0


@pytest.mark.unit
async def test_inv11_caller_id_alone_never_advances() -> None:
    """Inv-11 caller-id-alone: no grant back → the turn ends, nothing advances.

    The caller gets another go at reading the code (bounded — see
    test_verification_attempts_are_bounded), but "another go" must never mean
    capture_fields: without a grant the mutation does not move an inch.
    """
    handler = RecordingHandler(verify_body=_failed_body(), verify_status=400)
    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id="sess_vi_005",
        operation=OperationType.modify,
        verification_id=_VERIFICATION_ID,
    )

    result = await verify_identity(state)

    assert result["verification_outcome"] == "code_retry"
    assert "verification_grant_id" not in result
    assert handler.submit_call_count() == 0

    updated_state = AgentState(**{**state.model_dump(), **result})
    assert route_after_verify_identity(updated_state) == END


@pytest.mark.unit
async def test_expiry_enforced_ends_turn_for_reverify() -> None:
    """Expiry enforced: expired grant → outcome 'expired', grant unset, turn ENDs.

    The node does NOT escalate to human_handoff — the customer can retry once
    the grant is renewed — but it also cannot continue this turn, because the
    renewal has to come from them.
    """
    # Grant expires_at is in the past (relative to mock "now" inside the node)
    # We use a clearly past timestamp so it will always be expired
    handler = RecordingHandler(
        verify_body=_granted_body(expires_at="2020-01-01T00:00:00+00:00")
    )
    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id="sess_vi_006",
        operation=OperationType.modify,
        verification_id=_VERIFICATION_ID,
    )

    result = await verify_identity(state)

    assert result["verification_outcome"] == "expired", (
        "Expired grant must produce 'expired' outcome"
    )
    assert result.get("verification_grant_id") is None, (
        "Expired grant must leave verification_grant_id unset"
    )
    assert "terminal_state" not in result or result.get("terminal_state") is None, (
        "Expired grant must NOT set terminal_state (re-verify, not escalate)"
    )

    updated_state = AgentState(**{**state.model_dump(), **result})
    assert route_after_verify_identity(updated_state) == END, (
        "Expired grant ends the turn: the renewed code comes from the customer"
    )


@pytest.mark.unit
async def test_no_operation_id_minted_by_verify_identity() -> None:
    """VerifyIdentity must never set/advance operation_id (02-J owns that)."""
    handler = RecordingHandler(verify_body=_granted_body())
    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id="sess_vi_007",
        operation=OperationType.modify,
        verification_id=_VERIFICATION_ID,
        operation_id=None,  # starts unset
    )

    result = await verify_identity(state)

    # Result must not contain operation_id at all, or must have None
    assert result.get("operation_id") is None, (
        "verify_identity must NOT mint/advance operation_id"
    )
    # Only check_verification should have been called
    assert handler.submit_call_count() == 0, (
        "verify_identity must issue only check_verification, not submit"
    )


@pytest.mark.unit
async def test_cancel_operation_requires_cancel_action() -> None:
    """Cancel operation requires CANCEL action — MODIFY grant → INVALID."""
    handler = RecordingHandler(
        verify_body=_granted_body(allowed_action="modify")  # wrong for cancel
    )
    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id="sess_vi_008",
        operation=OperationType.cancel,
        verification_id=_VERIFICATION_ID,
    )

    result = await verify_identity(state)

    assert result["verification_outcome"] == "invalid"
    assert result.get("terminal_state") == "human_handoff"


@pytest.mark.unit
async def test_reschedule_requires_modify_action() -> None:
    """Reschedule is treated as a modify — CANCEL grant → INVALID."""
    handler = RecordingHandler(
        verify_body=_granted_body(allowed_action="cancel")  # wrong for reschedule
    )
    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id="sess_vi_009",
        operation=OperationType.reschedule,
        verification_id=_VERIFICATION_ID,
    )

    result = await verify_identity(state)

    assert result["verification_outcome"] == "invalid"
    assert result.get("terminal_state") == "human_handoff"


@pytest.mark.unit
async def test_reschedule_with_modify_grant_advances() -> None:
    """Reschedule is treated as a modify — MODIFY grant → VALID, advances."""
    handler = RecordingHandler(verify_body=_granted_body(allowed_action="modify"))
    set_tool_client(_make_client(handler))

    state = AgentState(
        tenant_id=_TENANT_ID,
        session_id="sess_vi_010",
        operation=OperationType.reschedule,
        verification_id=_VERIFICATION_ID,
    )

    result = await verify_identity(state)

    assert result["verification_outcome"] == "valid"
    assert result["verification_grant_id"] == "grant_001"


# ---------------------------------------------------------------------------
# Spoken-code capture
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    ("said", "expected"),
    [
        ("the code is 481502", "481502"),
        ("four eight one five oh two", "481502"),
        ("it's 48 15 02", "481502"),
        ("4 8 1 um 5 0 2", "481502"),
        ("sure, 4815", "4815"),
        ("there'll be four of us", None),
        ("I haven't got it yet", None),
        ("", None),
    ],
)
def test_extract_verification_code(said: str, expected: str | None) -> None:
    """Digits come back exactly, or not at all — never approximately."""
    assert extract_verification_code(said) == expected


@pytest.mark.unit
def test_extract_verification_code_does_not_glue_separate_numbers() -> None:
    """A number later in the turn must not be concatenated onto the code."""
    assert extract_verification_code("481502, and make it 6 people") == "481502"


# ---------------------------------------------------------------------------
# Turn 1 — request a code
# ---------------------------------------------------------------------------


class VerificationFlowHandler:
    """Fake API covering POST /api/verifications and the check endpoints."""

    def __init__(
        self,
        *,
        sent_body: dict[str, Any] | None = None,
        sent_status: int = 200,
        verify_body: dict[str, Any] | None = None,
        verify_status: int = 200,
    ) -> None:
        self.requests: list[httpx.Request] = []
        self._sent_body = (
            sent_body
            if sent_body is not None
            else {
                "status": "sent",
                "verification": {
                    "verification_id": _VERIFICATION_ID,
                    "channel": "sms",
                    "expires_at": "2099-01-01T00:00:00+00:00",
                    "masked_destination": "verified sms destination",
                },
            }
        )
        self._sent_status = sent_status
        self._verify_body = verify_body or {"status": "granted", "grant": _grant_obj()}
        self._verify_status = verify_status

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        if request.url.path == "/api/verifications":
            return _json_response(self._sent_status, self._sent_body)
        if "/api/verifications/" in request.url.path:
            return _json_response(self._verify_status, self._verify_body)
        return _json_response(404, {"error": "not_found"})

    def sent_payloads(self) -> list[dict[str, Any]]:
        return [
            json.loads(r.content)
            for r in self.requests
            if r.url.path == "/api/verifications"
        ]

    def verify_payloads(self) -> list[dict[str, Any]]:
        return [
            json.loads(r.content)
            for r in self.requests
            if r.url.path.endswith("/verify")
        ]


def _grant_obj(**overrides: Any) -> dict[str, Any]:
    """The nested grant object the real CheckVerificationResponse carries."""
    body = {
        "grant_id": "grant_001",
        "tenant_id": _TENANT_ID,
        "customer_id": "cust_001",
        "target_booking_id": None,
        "allowed_action": "modify",
        "expires_at": (
            datetime.datetime.now(datetime.UTC) + datetime.timedelta(days=1)
        ).isoformat(),
        "scope": "session",
    }
    body.update(overrides)
    return body


def _mutation_state(**overrides: Any) -> AgentState:
    base: dict[str, Any] = {
        "tenant_id": _TENANT_ID,
        "session_id": "sess_vi_flow",
        "operation": OperationType.modify,
    }
    base.update(overrides)
    return AgentState(**base)


@pytest.mark.unit
async def test_first_entry_requests_a_code_and_ends_the_turn() -> None:
    """No verification_id → send a code, say so, and wait for the caller.

    Before this, nothing in the graph ever called request_verification, so
    verification_id stayed None and every mutation was escalated to a human.
    """
    handler = VerificationFlowHandler()
    set_tool_client(_make_client(handler))

    result = await verify_identity(_mutation_state())

    assert result["verification_id"] == _VERIFICATION_ID
    assert result["verification_outcome"] == "code_sent"
    assert "code" in result["pending_verification_prompt"].lower()
    assert len(handler.sent_payloads()) == 1

    updated = AgentState(**{**_mutation_state().model_dump(), **result})
    assert route_after_verify_identity(updated) == END


@pytest.mark.unit
async def test_send_key_is_stable_for_session_and_action() -> None:
    """Inv-9: the same session + action + round reuses one idempotency key."""
    handler = VerificationFlowHandler()
    set_tool_client(_make_client(handler))

    await verify_identity(_mutation_state())
    await verify_identity(_mutation_state())

    keys = {p["verification_idempotency_key"] for p in handler.sent_payloads()}
    assert len(keys) == 1
    key = keys.pop()
    assert "sess_vi_flow" in key
    assert "modify" in key


@pytest.mark.unit
async def test_send_carries_the_target_booking() -> None:
    """The code goes to the phone on the BOOKING, so the target is sent with it."""
    handler = VerificationFlowHandler()
    set_tool_client(_make_client(handler))

    draft = BookingDraft(
        tenant_id=_TENANT_ID,
        session_id="sess_vi_flow",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.cancel, target_booking_id="bk_777"
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )
    await verify_identity(_mutation_state(operation=OperationType.cancel, draft=draft))

    payload = handler.sent_payloads()[0]
    assert payload["target_booking_id"] == "bk_777"
    assert payload["action"] == "cancel"


@pytest.mark.unit
async def test_unsendable_code_escalates() -> None:
    """No verified destination → there is no route to a grant; hand off (Inv-11)."""
    handler = VerificationFlowHandler(
        sent_body={"status": "no_verified_destination", "verification": None}
    )
    set_tool_client(_make_client(handler))

    result = await verify_identity(_mutation_state())

    assert result["verification_outcome"] == "invalid"
    assert result["terminal_state"] == "human_handoff"
    assert "verification_grant_id" not in result


# ---------------------------------------------------------------------------
# Turn 2 — the caller reads the code out
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_spoken_code_is_submitted_and_grants_advance() -> None:
    """The digits in the transcript are POSTed and the grant advances the dialog."""
    handler = VerificationFlowHandler()
    set_tool_client(_make_client(handler))

    state = _mutation_state(
        verification_id=_VERIFICATION_ID,
        verification_outcome="code_sent",
        transcript_text="yeah it's four eight one five oh two",
    )
    result = await verify_identity(state)

    assert handler.verify_payloads() == [{"code": "481502"}]
    assert result["verification_grant_id"] == "grant_001"
    assert result["verification_outcome"] == "valid"

    updated = AgentState(**{**state.model_dump(), **result})
    assert route_after_verify_identity(updated) == "capture_fields"


@pytest.mark.unit
async def test_wrong_code_re_asks_without_advancing() -> None:
    """A wrong code is a re-ask, not a handoff — and never an advance (Inv-11)."""
    handler = VerificationFlowHandler(verify_body={"status": "failed", "grant": None})
    set_tool_client(_make_client(handler))

    state = _mutation_state(
        verification_id=_VERIFICATION_ID,
        verification_outcome="code_sent",
        transcript_text="481503",
    )
    result = await verify_identity(state)

    assert result["verification_outcome"] == "code_retry"
    assert result["verification_attempts"] == 1
    assert "verification_grant_id" not in result
    assert result["pending_verification_prompt"]

    updated = AgentState(**{**state.model_dump(), **result})
    assert route_after_verify_identity(updated) == END


@pytest.mark.unit
async def test_verification_attempts_are_bounded() -> None:
    """A caller who cannot read the code back belongs with a human (Inv-11)."""
    handler = VerificationFlowHandler(verify_body={"status": "failed", "grant": None})
    set_tool_client(_make_client(handler))

    state = _mutation_state(
        verification_id=_VERIFICATION_ID,
        verification_outcome="code_retry",
        verification_attempts=MAX_VERIFICATION_ATTEMPTS - 1,
        transcript_text="481503",
    )
    result = await verify_identity(state)

    assert result["verification_outcome"] == "invalid"
    assert result["terminal_state"] == "human_handoff"

    updated = AgentState(**{**state.model_dump(), **result})
    assert route_after_verify_identity(updated) == "human_handoff"


@pytest.mark.unit
async def test_expired_challenge_clears_the_verification_for_a_fresh_code() -> None:
    """An aged-out code cannot be revived; the next turn sends a new one."""
    handler = VerificationFlowHandler(verify_body={"status": "expired", "grant": None})
    set_tool_client(_make_client(handler))

    state = _mutation_state(
        verification_id=_VERIFICATION_ID,
        verification_outcome="code_sent",
        transcript_text="481502",
    )
    result = await verify_identity(state)

    assert result["verification_id"] is None
    assert result["verification_outcome"] == "expired"
    assert "expired" in result["pending_verification_prompt"].lower()

    updated = AgentState(**{**state.model_dump(), **result})
    assert route_after_verify_identity(updated) == END


@pytest.mark.unit
async def test_prompt_is_wired_into_what_the_caller_hears() -> None:
    """The node ENDs the turn, so a silent verification turn is dead air."""
    assert REPLY_TEXT_KEYS["verify_identity"] == "pending_verification_prompt"
    assert (
        segment_for("verify_identity", {"pending_verification_prompt": "code sent"})
        == "code sent"
    )
