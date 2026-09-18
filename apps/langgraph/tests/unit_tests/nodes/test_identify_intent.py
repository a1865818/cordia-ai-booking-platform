"""Unit tests for the identify_intent node and classify_intent function (02-F).

Verifies representative utterances → each of create/reschedule/modify/cancel/inquire.
"""

from __future__ import annotations

import json

import httpx
import pytest

from cordia_agent.nodes.identify_intent import classify_intent, identify_intent
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    Channel,
    CustomerRef,
    DialogIntent,
    OperationType,
    SourceInfo,
)
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client


# ---------------------------------------------------------------------------
# classify_intent (pure function tests)
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    "text,expected",
    [
        # create
        ("I'd like to book a table for two", DialogIntent.create),
        ("Can I reserve an appointment?", DialogIntent.create),
        ("I need to make a reservation", DialogIntent.create),
        # reschedule
        ("I need to reschedule my appointment", DialogIntent.reschedule),
        ("Can you move my booking to tomorrow?", DialogIntent.reschedule),
        ("I'd like to change time for my booking", DialogIntent.reschedule),
        ("Is there a different time available?", DialogIntent.reschedule),
        # modify
        ("I want to modify the party size", DialogIntent.modify),
        ("Can I update the booking details?", DialogIntent.modify),
        ("I'd like to edit my reservation", DialogIntent.modify),
        # cancel
        ("I want to cancel my booking", DialogIntent.cancel),
        ("Please process a cancellation for me", DialogIntent.cancel),
        ("I need to cancel", DialogIntent.cancel),
        # inquire
        ("What are your opening hours?", DialogIntent.inquire),
        ("When is the next available slot?", DialogIntent.inquire),
        ("Do you have appointments on Monday?", DialogIntent.inquire),
        ("Is the spa open on weekends?", DialogIntent.inquire),
        ("How long does a haircut take?", DialogIntent.inquire),
    ],
)
def test_classify_intent_representative_utterances(
    text: str, expected: DialogIntent
) -> None:
    """Each representative utterance maps to the expected DialogIntent."""
    result = classify_intent(text)
    assert result == expected, (
        f"classify_intent({text!r}) → {result!r}, expected {expected!r}"
    )


@pytest.mark.unit
def test_classify_intent_cancel_precedes_reschedule() -> None:
    """'cancel' takes precedence over 'reschedule' in the same utterance."""
    # "cancel my reschedule" — cancel keyword appears; cancel has higher precedence.
    result = classify_intent("cancel my reschedule request")
    assert result == DialogIntent.cancel


@pytest.mark.unit
def test_classify_intent_reschedule_precedes_modify() -> None:
    """'reschedule' takes precedence over 'change' (modify keyword)."""
    result = classify_intent("I want to reschedule and change the time")
    assert result == DialogIntent.reschedule


@pytest.mark.unit
def test_classify_intent_empty_text_returns_inquire() -> None:
    """Empty text falls through to the default: inquire."""
    result = classify_intent("")
    assert result == DialogIntent.inquire


@pytest.mark.unit
def test_classify_intent_case_insensitive() -> None:
    """Classification is case-insensitive."""
    assert classify_intent("CANCEL MY BOOKING") == DialogIntent.cancel
    assert classify_intent("Book A Table") == DialogIntent.create
    assert classify_intent("RESCHEDULE please") == DialogIntent.reschedule


# ---------------------------------------------------------------------------
# identify_intent node (async)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_identify_intent_sets_dialog_intent() -> None:
    """identify_intent sets dialog_intent on the returned update dict."""
    state = AgentState(transcript_text="I'd like to book a table")
    result = await identify_intent(state)
    assert result["dialog_intent"] == DialogIntent.create


@pytest.mark.unit
async def test_identify_intent_sets_operation_for_booking_intents() -> None:
    """For booking intents, identify_intent also sets the operation."""
    state = AgentState(transcript_text="I need to cancel my appointment")
    result = await identify_intent(state)
    assert result["dialog_intent"] == DialogIntent.cancel
    assert result["operation"] == OperationType.cancel


@pytest.mark.unit
async def test_identify_intent_does_not_set_operation_for_inquire() -> None:
    """For inquire, identify_intent does NOT set operation (None means no booking op)."""
    state = AgentState(transcript_text="What are your opening hours?")
    result = await identify_intent(state)
    assert result["dialog_intent"] == DialogIntent.inquire
    assert "operation" not in result


@pytest.mark.unit
async def test_identify_intent_reschedule_sets_correct_operation() -> None:
    """reschedule intent sets OperationType.reschedule on state."""
    state = AgentState(transcript_text="I want to reschedule my booking")
    result = await identify_intent(state)
    assert result["dialog_intent"] == DialogIntent.reschedule
    assert result["operation"] == OperationType.reschedule


@pytest.mark.unit
async def test_identify_intent_handles_none_transcript() -> None:
    """identify_intent handles None transcript_text gracefully."""
    state = AgentState(transcript_text=None)
    result = await identify_intent(state)
    # Empty text defaults to inquire
    assert result["dialog_intent"] == DialogIntent.inquire


# ---------------------------------------------------------------------------
# Fix C — identify_intent builds an initial BookingDraft when draft is None
#          and session bootstrap fields are present
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_identify_intent_builds_initial_draft_when_session_ready() -> None:
    """Fix C: when draft is None and source/tenant_id/session_id are set, an initial
    BookingDraft is constructed with request.operation == OperationType.create.
    """
    state = AgentState(
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
        tenant_id="tenant_fix_c",
        session_id="sess_fix_c",
        tenant_policy_version="v1",
        draft=None,
        transcript_text="I'd like to book a table",
    )

    result = await identify_intent(state)

    draft = result.get("draft")
    assert draft is not None, (
        "Fix C: identify_intent must construct an initial BookingDraft "
        "when source/tenant_id/session_id are present and draft is None"
    )
    assert isinstance(draft, BookingDraft)
    assert draft.request.operation == OperationType.create, (
        f"Fix C: initial draft.request.operation must be create; got {draft.request.operation!r}"
    )


@pytest.mark.unit
async def test_identify_intent_puts_the_customer_on_the_request_not_only_the_draft() -> (
    None
):
    """The revision is built from ``draft.request`` (read_back passes it straight to
    build_revision as the canonical payload), so a customer kept only on the draft never
    reaches the hashed payload the API receives — the booking would land with no
    notification destination and be unfindable by phone. identify_intent is the only site
    that can set it.
    """
    customer = CustomerRef(customer_id="cust_fix", display_name="A. Guest")
    state = AgentState(
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
        tenant_id="tenant_fix_c",
        session_id="sess_fix_c",
        tenant_policy_version="v1",
        draft=None,
        customer=customer,
        transcript_text="I'd like to book a table",
    )

    result = await identify_intent(state)

    draft = result.get("draft")
    assert draft is not None
    assert draft.request.customer == customer, (
        "the customer must ride on draft.request (the hashed canonical payload), "
        f"not only on the draft; got {draft.request.customer!r}"
    )
    assert draft.customer == customer


@pytest.mark.unit
async def test_identify_intent_defers_draft_when_source_missing() -> None:
    """Fix C control: when source is None, no initial draft is built (deferred)."""
    state = AgentState(
        source=None,
        tenant_id="tenant_fix_c",
        session_id="sess_fix_c",
        draft=None,
        transcript_text="I'd like to book a table",
    )

    result = await identify_intent(state)

    # dialog_intent and operation are still set; draft must be absent.
    assert result["dialog_intent"] == DialogIntent.create
    assert "draft" not in result, (
        "Fix C control: with source=None the draft key must not appear in the result"
    )


# ---------------------------------------------------------------------------
# Fix E — identify_intent revalidates the draft's request on reclassify.
#         A mutation with no target is VALID now (the target arrives later in
#         the dialog); only a genuinely malformed request is rejected.
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_identify_intent_reclassifies_to_cancel_without_target() -> None:
    """Reclassifying a create draft to cancel with no target must SUCCEED.

    This used to be rejected, and rejecting it is the bug: "actually, cancel it"
    left the draft on operation=create, so the dialog went on gathering booking
    details for a booking the caller wanted rid of.  The target is asked for
    afterwards and enforced at submission.
    """
    existing_draft = BookingDraft(
        tenant_id="tenant_fix_e",
        session_id="sess_fix_e",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(operation=OperationType.create),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )

    state = AgentState(
        tenant_id="tenant_fix_e",
        session_id="sess_fix_e",
        draft=existing_draft,
        transcript_text="cancel my booking",
    )

    result = await identify_intent(state)

    returned_draft = result.get("draft")
    assert returned_draft is not None, (
        "a cancellation must be draftable before the caller has named the booking"
    )
    assert returned_draft.request.operation == OperationType.cancel
    assert returned_draft.request.target_booking_id is None
    assert result["operation"] == OperationType.cancel


@pytest.mark.unit
async def test_identify_intent_reclassifies_cancel_when_target_present() -> None:
    """Fix E positive control: reclassify succeeds when target_booking_id is set.

    An existing create draft WITH target_booking_id triggers the revalidation path;
    the result draft's operation must be cancel (revalidation passes).
    """
    existing_draft = BookingDraft(
        tenant_id="tenant_fix_e",
        session_id="sess_fix_e",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.create,
            target_booking_id="bk_existing_001",
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )

    state = AgentState(
        tenant_id="tenant_fix_e",
        session_id="sess_fix_e",
        draft=existing_draft,
        transcript_text="cancel my booking",
    )

    result = await identify_intent(state)

    returned_draft = result.get("draft")
    assert returned_draft is not None, (
        "Fix E positive: reclassify to cancel with target_booking_id must succeed "
        "and return the updated draft"
    )
    assert returned_draft.request.operation == OperationType.cancel, (
        f"Fix E positive: expected operation=cancel; got {returned_draft.request.operation!r}"
    )


# ---------------------------------------------------------------------------
# Defect 1 — "I need to cancel my booking" must produce a draft
# ---------------------------------------------------------------------------


_TENANT = "tenant_ii_test"

_FAKE_TENANT_CONTEXT = TenantContext(
    tenant_id=_TENANT,
    channel=Channel.voice,
    channel_endpoint_id="ep_ii_001",
    configuration_version="v1",
    correlation_id="corr_ii_001",
    session_id="sess_ii_001",
    allowed_agent_policy_ids=["policy_a"],
    allowed_tool_policy_ids=["policy_a"],
    issued_at="2026-07-25T00:00:00Z",
    expires_at="2026-07-25T01:00:00Z",
)


class DiscoveryHandler:
    """Fake API for GET /api/bookings and GET /api/bookings/{id}."""

    def __init__(self, candidates: list[dict], version: int | None = 7) -> None:
        self.requests: list[httpx.Request] = []
        self._candidates = candidates
        self._version = version

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.requests.append(request)
        body: dict
        if request.url.path == "/api/bookings":
            status = (
                "none"
                if not self._candidates
                else ("found" if len(self._candidates) == 1 else "multiple")
            )
            body = {"status": status, "candidates": self._candidates}
        elif request.url.path.startswith("/api/bookings/"):
            body = {
                "status": "ok",
                "booking": {
                    "booking_id": request.url.path.rsplit("/", 1)[-1],
                    "booking_reference": "CORD-1",
                    "status": "confirmed",
                    "starts_at": "2026-06-15T19:00:00+00:00",
                    "party_size": 4,
                    "version": self._version,
                },
            }
        else:
            return httpx.Response(404, json={"error": "not_found"})
        return httpx.Response(
            200,
            headers={"content-type": "application/json"},
            content=json.dumps(body).encode(),
        )

    def lookup_count(self) -> int:
        return sum(1 for r in self.requests if r.url.path == "/api/bookings")


def _candidate(booking_id: str) -> dict:
    return {
        "booking_id": booking_id,
        "booking_reference": f"CORD-{booking_id}",
        "business": "The Fitzroy",
        "date": "2026-06-15",
        "approx_time": "7:00 PM",
        "party_size": 4,
    }


def _install(handler) -> None:
    http = httpx.AsyncClient(
        transport=httpx.MockTransport(handler), base_url="http://test"
    )
    set_tool_client(ToolClient(http, tenant_context=_FAKE_TENANT_CONTEXT))


@pytest.fixture(autouse=True)
def _reset_tool_client():
    yield
    reset_tool_client()


def _cancel_state(**overrides) -> AgentState:
    base = {
        "tenant_id": _TENANT,
        "session_id": "sess_ii_001",
        "source": SourceInfo(channel=Channel.voice, language="en-AU"),
        "transcript_text": "I need to cancel my booking",
    }
    base.update(overrides)
    return AgentState(**base)


@pytest.mark.unit
async def test_cancel_produces_a_draft_with_no_target() -> None:
    """The dead end: no draft meant capture_fields and clarify both no-opped.

    With no caller number there is nothing to look up, so the draft is created
    with the target still empty and clarify goes on to ask for the reference.
    """
    result = await identify_intent(_cancel_state())

    draft = result["draft"]
    assert draft.request.operation == OperationType.cancel
    assert draft.request.target_booking_id is None


@pytest.mark.unit
async def test_caller_id_prefills_a_single_booking() -> None:
    """One booking on the caller's number: do not make them read a reference out."""
    handler = DiscoveryHandler([_candidate("bk_1")], version=7)
    _install(handler)

    result = await identify_intent(
        _cancel_state(customer=CustomerRef(phone_e164="+61477000111"))
    )

    draft = result["draft"]
    assert draft.request.target_booking_id == "bk_1"
    assert draft.request.target_booking_version == 7
    assert draft.field_provenance["target_booking_id"].capture_method.value == (
        "channel_verified"
    )


@pytest.mark.unit
@pytest.mark.parametrize(
    "candidates",
    [[], [_candidate("bk_1"), _candidate("bk_2")]],
)
async def test_ambiguous_lookup_leaves_the_target_for_clarify(
    candidates: list[dict],
) -> None:
    """Zero or several candidates: ask, never guess (spec 08 §9.4.1)."""
    _install(DiscoveryHandler(candidates))

    result = await identify_intent(
        _cancel_state(customer=CustomerRef(phone_e164="+61477000111"))
    )

    assert result["draft"].request.target_booking_id is None
    assert result["target_lookup_attempted"] is True


@pytest.mark.unit
async def test_lookup_is_not_repeated_every_turn() -> None:
    """The lookup is a read, but a read per turn for the rest of the call."""
    handler = DiscoveryHandler([])
    _install(handler)

    draft = BookingDraft(
        tenant_id=_TENANT,
        session_id="sess_ii_001",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(operation=OperationType.create),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )
    state = _cancel_state(
        customer=CustomerRef(phone_e164="+61477000111"),
        draft=draft,
        target_lookup_attempted=True,
    )

    await identify_intent(state)

    assert handler.lookup_count() == 0


@pytest.mark.unit
async def test_a_spoken_code_is_not_reclassified() -> None:
    """ "481502" classifies as inquire, and answering an inquiry ENDs the turn.

    Holding the operation steady is what keeps the turn on its way to
    verify_identity instead of stranding the cancellation.
    """
    draft = BookingDraft(
        tenant_id=_TENANT,
        session_id="sess_ii_001",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(
            operation=OperationType.cancel, target_booking_id="bk_1"
        ),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )
    state = _cancel_state(
        transcript_text="481502",
        draft=draft,
        operation=OperationType.cancel,
        verification_id="verif_001",
        verification_outcome="code_sent",
    )

    result = await identify_intent(state)

    assert result["dialog_intent"] == DialogIntent.cancel
    assert result["operation"] == OperationType.cancel
    assert "draft" not in result
