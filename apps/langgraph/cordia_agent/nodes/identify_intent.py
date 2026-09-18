"""Node: identify_intent — §8.3 Intent Identification.

Classifies the customer's utterance into one of the five :class:`DialogIntent`
values (create / reschedule / modify / cancel / inquire) using a deterministic
keyword-based stub.  A real model-backed classifier replaces this in a later
ticket.

Intent-classification precedence (highest → lowest):
  1. ``cancel``    — "cancel", "cancellation", "remove booking"
  2. ``reschedule`` — "reschedule", "move", "change time", "change the time",
                       "different time"
  3. ``modify``    — "change", "modify", "update", "edit", "amend"
  4. ``create``    — "book", "reserve", "table", "make a"
                     ("appointment" is intentionally omitted — too ambiguous;
                      "book/reserve/make a" cover "book/make an appointment")
  5. ``inquire``   — question words / informational phrases with no booking verb
                     ("what", "when", "hours", "available", "do you",
                      "how", "is", "?")

The precedence ensures that "cancel my reschedule" → ``cancel`` and
"reschedule my booking" → ``reschedule`` rather than falling through to
``modify`` (which also uses the word "change").

When the classified :class:`DialogIntent` maps to a booking
:class:`~cordia_agent.state.OperationType` (anything except ``inquire``),
``state.operation`` is also set.  If a draft exists, its ``request.operation``
is updated to match.
"""

from __future__ import annotations

import logging
from datetime import datetime
from typing import TYPE_CHECKING
from zoneinfo import ZoneInfo

from pydantic import ValidationError

from cordia_agent.readback.renderer import _SPOKEN_NUMBERS, format_clock
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    CaptureMethod,
    DialogIntent,
    FieldProvenance,
    OperationType,
)
from cordia_agent.tools.client_binding import (
    find_manageable_bookings_call,
    get_booking_details_call,
    parse_booking_projection,
    parse_manageable_bookings,
)


if TYPE_CHECKING:
    from cordia_agent.tools.contracts import ManageableBooking


_log = logging.getLogger(__name__)


# Draft schema version stamped on a freshly-constructed BookingDraft.
_DRAFT_SCHEMA_VERSION = "1.0.0"


# ---------------------------------------------------------------------------
# Keyword rules
# ---------------------------------------------------------------------------

# Each entry is (keywords, DialogIntent).  The list is ordered by precedence.
_RULES: list[tuple[tuple[str, ...], DialogIntent]] = [
    # 1. Cancel
    (
        ("cancel", "cancellation", "remove booking"),
        DialogIntent.cancel,
    ),
    # 2. Reschedule (checked before modify because "change" also appears in reschedule phrases)
    (
        ("reschedule", "move", "change time", "change the time", "different time"),
        DialogIntent.reschedule,
    ),
    # 3. Modify (generic change/update without a specific time reference)
    (
        ("change", "modify", "update", "edit", "amend"),
        DialogIntent.modify,
    ),
    # 4. Create — "appointment" is omitted (ambiguous; "make a" or "book" cover it)
    (
        ("book", "reserve", "table", "make a"),
        DialogIntent.create,
    ),
    # 5. Inquire — informational phrases (no booking verb required)
    (
        ("what", "when", "hours", "available", "do you", "how", "is ", "?"),
        DialogIntent.inquire,
    ),
]

# Mapping from DialogIntent to OperationType (inquire has no operation).
_INTENT_TO_OPERATION: dict[DialogIntent, OperationType] = {
    DialogIntent.create: OperationType.create,
    DialogIntent.reschedule: OperationType.reschedule,
    DialogIntent.modify: OperationType.modify,
    DialogIntent.cancel: OperationType.cancel,
}

# The reverse mapping, used when a turn must NOT be reclassified: an operation
# already in flight tells us what the dialog is about better than a fragment of
# an answer does.
_OPERATION_TO_INTENT: dict[OperationType, DialogIntent] = {
    operation: intent for intent, operation in _INTENT_TO_OPERATION.items()
}

# The operations that act on a booking that already exists.
_MUTATIONS: frozenset[OperationType] = frozenset(
    {OperationType.modify, OperationType.cancel, OperationType.reschedule}
)

# Outcomes that mean verify_identity is waiting for the caller to read a code
# back to us.
_AWAITING_CODE_OUTCOMES: frozenset[str] = frozenset({"code_sent", "code_retry"})


# ---------------------------------------------------------------------------
# Classifier (pure function — testable without async)
# ---------------------------------------------------------------------------


def classify_intent(text: str) -> DialogIntent:
    """Classify *text* into a :class:`DialogIntent` using deterministic keyword rules.

    Applies the precedence table in order; returns the intent of the first
    rule that matches.  Falls back to :attr:`DialogIntent.inquire` when no
    booking-verb rule matches (treating ambiguous input as informational).

    Args:
        text: The transcript utterance to classify (case-insensitive).

    Returns:
        The matched :class:`DialogIntent`.
    """
    lower = text.lower()
    for keywords, intent in _RULES:
        for kw in keywords:
            if kw in lower:
                return intent
    # Default: treat unknown utterances as informational.
    return DialogIntent.inquire


# ---------------------------------------------------------------------------
# Node
# ---------------------------------------------------------------------------


def awaiting_verification_code(state: AgentState) -> bool:
    """Whether the caller is mid-way through reading a verification code back.

    A code is four digits with nothing conversational about it, so the keyword
    classifier calls it ``inquire`` and the router hands it to answer_inquiry,
    which ENDs the turn.  The cancellation the caller asked for is then stranded
    one step from being verified.  When we are waiting for a code, the turn is
    an answer to OUR question and is not reclassified at all.

    Args:
        state: Current ``AgentState``.

    Returns:
        ``True`` when verify_identity has sent a code and has not yet had a
        valid one back.
    """
    return (
        state.verification_id is not None
        and state.verification_grant_id is None
        and (state.verification_outcome or "") in _AWAITING_CODE_OUTCOMES
    )


async def _discover_target_from_caller_id(state: AgentState) -> dict | None:
    """Find the caller's booking from their own number, if there is exactly one.

    Being asked "which booking?" by a venue you have just rung from the number
    you booked with is the kind of question that makes an agent feel like a
    form.  When the channel gave us the caller's number we look their bookings
    up and, if precisely one comes back, fill the target in so the dialog goes
    straight to reading that booking back for confirmation.

    Zero or several candidates leave the target unset deliberately: clarify then
    asks for the reference, which is the only honest question at that point.
    Picking one of several would guess at another booking, and spec 08 §9.4.1
    forbids surfacing candidates the caller has not identified themselves.

    This is discovery, NOT authorization (MB-Inv-1/Inv-11): the grant gate in
    verify_identity still runs in full before anything is submitted.

    Args:
        state: Current ``AgentState`` (read for the caller's number).

    Returns:
        ``{"target_booking_id": ..., "target_booking_version": ...}`` on a single
        confident match, otherwise ``None``.
    """
    phone = state.customer.phone_e164 if state.customer else None
    if not phone:
        return None

    candidates = parse_manageable_bookings(
        await find_manageable_bookings_call(phone_e164=phone)
    )
    if len(candidates) != 1:
        _log.info(
            "caller-ID booking discovery found %d candidates; asking for the reference",
            len(candidates),
        )
        return None

    booking_id = candidates[0].booking_id
    # The version is the optimistic-concurrency token a mutation must echo back
    # (spec 08 §9.4.8); the discovery row does not carry it, the minimal
    # projection does, and it is available without a grant.
    projection = parse_booking_projection(await get_booking_details_call(booking_id))
    return {
        "target_booking_id": booking_id,
        "target_booking_version": projection.version if projection else None,
        # Not a request field: the words read_back uses to say WHICH booking.
        # Without it the cancellation read-back was "that's cancelling your
        # booking", which asks the caller to confirm a thing it has not named.
        "_target_label": _describe_booking(candidates[0], state.tenant_timezone),
    }


def _describe_booking(candidate: ManageableBooking, timezone: str | None) -> str:
    """ "your table for two on Wednesday 16 September at 7pm".

    ``approx_time`` arrives as a UTC wall clock ("09:00" for a 7pm Melbourne
    table), so it is re-anchored to the venue's zone before anyone hears it,
    exactly as read_back does for a new booking.  Formatting mirrors
    readback.renderer so a cancellation and a booking sound like the same
    person said them.
    """
    party = _SPOKEN_NUMBERS.get(candidate.party_size, str(candidate.party_size))
    who = f"your table for {party}"
    try:
        when_utc = datetime.fromisoformat(
            f"{candidate.date}T{candidate.approx_time}:00+00:00"
        )
    except ValueError:
        return who
    local = when_utc.astimezone(ZoneInfo(timezone)) if timezone else when_utc
    return f"{who} on {local:%A %-d %B} at {format_clock(local)}"


def _with_target(draft: BookingDraft, target: dict) -> BookingDraft:
    """Return *draft* with the discovered target merged into its request.

    The target is recorded with ``CaptureMethod.channel_verified`` provenance:
    it came from the channel's caller ID, not from anything the caller said, and
    the provenance is the only place that distinction survives.
    """
    target = dict(target)
    label = target.pop("_target_label", None)
    data = draft.request.model_dump(mode="python")
    data.update(target)
    request = BookingRequest.model_validate(data)
    provenance = dict(draft.field_provenance)
    provenance["target_booking_id"] = FieldProvenance(
        capture_method=CaptureMethod.channel_verified,
        normalized_value=target["target_booking_id"],
    )
    names = dict(draft.resolved_names)
    if label:
        names["target_booking_id"] = label
    return draft.model_copy(
        update={
            "request": request,
            "field_provenance": provenance,
            "resolved_names": names,
        }
    )


async def identify_intent(state: AgentState) -> dict:
    """Classify ``state.transcript_text`` and update intent + operation on state (§8.3).

    Sets ``dialog_intent`` on every call.  When the intent maps to a booking
    operation, also sets ``operation`` and, if a draft is active, updates
    ``draft.request.operation`` to match.

    Args:
        state: Current ``AgentState``.

    Returns:
        A partial state-update dict with ``dialog_intent`` and, for booking
        intents, ``operation`` (and — when buildable — the initial/updated ``draft``).
    """
    if awaiting_verification_code(state) and state.operation is not None:
        # Hold the intent steady while the caller reads the code out; see
        # awaiting_verification_code for why reclassifying here loses the call.
        return {
            "dialog_intent": _OPERATION_TO_INTENT.get(
                state.operation, DialogIntent.inquire
            ),
            "operation": state.operation,
        }

    text = state.transcript_text or ""
    intent = classify_intent(text)
    update: dict = {"dialog_intent": intent}

    operation = _INTENT_TO_OPERATION.get(intent)
    if operation is None:
        # ``inquire`` is informational — no booking operation or draft.
        return update

    update["operation"] = operation

    if state.draft is None:
        # Construct the initial BookingDraft so CaptureFields has something to
        # capture into — otherwise capture_fields no-ops and the graph loops
        # between capture_fields and clarify without reaching read-back/submit.
        # Requires the session bootstrap fields established upstream
        # (resolve_tenant / load_context / AgentInputState).
        if state.source is not None and state.tenant_id and state.session_id:
            # A mutation is drafted WITHOUT a target.  It used to be built
            # inside contextlib.suppress(ValidationError) because BookingRequest
            # rejected a mutation with no target_booking_id, so "I need to
            # cancel my booking" produced no draft at all and the caller heard
            # the "sorry, I did not catch that" fallback on every turn after.
            # The target is captured during the dialog and enforced at
            # submission — see BookingRequest.require_target_for_mutations.
            draft = BookingDraft(
                tenant_id=state.tenant_id,
                session_id=state.session_id,
                schema_version=_DRAFT_SCHEMA_VERSION,
                tenant_policy_version=state.tenant_policy_version or "unknown",
                # The guest goes on the REQUEST as well as the draft.
                #
                # The revision is built from ``draft.request`` (``read_back`` passes it
                # straight to ``build_revision`` as the canonical payload), so a customer kept
                # only on the draft never reaches the hashed payload the API receives. The
                # booking then lands with no notification destination and cannot be found by
                # ``GET /api/bookings?phone_e164=`` — the exact loss ``BookingRequest.customer``
                # was added to prevent. This is the only site that can set it: the reclassify
                # branch round-trips the existing request, and no later node writes it.
                request=BookingRequest(operation=operation, customer=state.customer),
                source=state.source,
                customer=state.customer,
            )
            if operation in _MUTATIONS:
                target = await _discover_target_from_caller_id(state)
                update["target_lookup_attempted"] = True
                if target is not None:
                    draft = _with_target(draft, target)
            update["draft"] = draft
        return update

    # A draft already exists: reclassify its operation through REVALIDATION.
    # model_copy(update=...) skips validators, so rebuild the request via
    # model_validate to type-check the change.
    data = state.draft.request.model_dump(mode="json")
    data["operation"] = operation.value
    try:
        new_request = BookingRequest.model_validate(data)
    except ValidationError:
        # The reclassified request did not validate; leave the draft untouched
        # rather than half-applying an operation change.
        _log.warning("reclassify to %s did not validate; draft left as-is", operation)
        return update
    draft = state.draft.model_copy(update={"request": new_request})

    # A dialog that TURNS INTO a mutation ("actually, cancel it") gets the same
    # caller-ID lookup the first draft would have got, once.
    if (
        operation in _MUTATIONS
        and new_request.target_booking_id is None
        and not state.target_lookup_attempted
    ):
        target = await _discover_target_from_caller_id(state)
        update["target_lookup_attempted"] = True
        if target is not None:
            draft = _with_target(draft, target)

    update["draft"] = draft
    return update
