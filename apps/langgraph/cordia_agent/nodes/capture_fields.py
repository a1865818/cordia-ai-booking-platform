"""Node: capture_fields — §8.4 Field Capture Loop.

Drives the slot-filling dialogue: extracts slots from the current transcript
via the :class:`~cordia_agent.slots.capture.SlotExtractor` port, applies them
to the draft via the constrained capture engine, and routes to clarify /
verify_identity / check_candidates / read_back once the draft is ready.

Invariant Inv-1 is enforced inside :func:`~cordia_agent.slots.capture.capture_slots`:
partial transcripts (``transcript_is_final=False``) leave the draft unchanged.

It is also where the venue gets to say no.  The policy gate (§6.1) runs here,
BEFORE availability, because "a table for 400 at 7pm" and "a table at 3am" are
not capacity questions and the capacity lookup answered both of them happily.

Material updates are persisted immediately via the module-default
:class:`~cordia_agent.checkpoint.SessionCheckpointer` (§8.9).
"""

from __future__ import annotations

from datetime import datetime

from cordia_agent.checkpoint import get_session_checkpointer
from cordia_agent.slots.capture import (
    capture_slots,
    get_slot_extractor,
    required_candidate_fields_present,
)
from cordia_agent.slots.local_time import reanchor_to_venue
from cordia_agent.slots.resolver import resolve_resource_names
from cordia_agent.slots.sitting import SittingOutcome, derive_sitting
from cordia_agent.state import AgentState, BookingDraft, OperationType, TriState
from cordia_agent.tools.client_binding import parse_policy_validation, validate_draft


#: State key carrying the venue's own reason for refusing this draft, so the
#: reply layer can say it instead of asking the next form field.
#:
#: Read through :func:`policy_block_message` rather than as an attribute:
#: ``AgentState`` gains the field in the same change that teaches ``clarify`` to
#: speak it, and both files are owned elsewhere.  Until they land LangGraph drops
#: the unknown channel with a warning and this reads ``None``, which is exactly
#: today's behaviour.  Once they land, make it a plain attribute read.
POLICY_BLOCK_KEY = "policy_block_message"

#: Said when the venue is open but no sitting covers the requested time.  The
#: sittings are NAMED from the tenant's own declared services, never guessed:
#: "we do breakfast, lunch and dinner" is a claim about this venue.
NOT_SERVING_TEMPLATE = (
    "We're not serving at that time, sorry. We do {options}. Which would suit?"
)

#: Said when the time is not served and the venue declares nothing to offer instead.
NOT_SERVING_UNKNOWN = (
    "We're not serving at that time, sorry. What time were you thinking?"
)


def policy_block_message(state: AgentState) -> str | None:
    """Return the venue's refusal message for the current draft, if any."""
    return state.policy_block_message


def _spoken_list(names: tuple[str, ...]) -> str:
    """Join *names* the way someone reads a short list aloud."""
    if len(names) == 1:
        return names[0]
    return ", ".join(names[:-1]) + f", and {names[-1]}"


def _has_policy_inputs(draft: BookingDraft) -> bool:
    """Return ``True`` when the draft holds something the policy gate can judge.

    The gate rules on a concrete party size and a concrete start time and on
    nothing else; asking it about a draft carrying neither spends a round trip of
    the caller's silence to be told, correctly, that nothing is wrong.
    """
    request = draft.request
    return any(
        value is not None and not isinstance(value, TriState)
        for value in (request.starts_at, request.party_size)
    )


async def _policy_block(draft: BookingDraft) -> str | None:
    """Ask the venue whether it will take this draft at all.

    "A table for 400 at 7pm" and "a table at 3am" both used to sail through to the
    availability lookup, which answers a capacity question and has no opinion on
    either.  The tool that does has existed the whole time with no caller.

    Returns:
        The customer-facing reason to refuse, or ``None`` when the draft passes.
        Also ``None`` when the check itself did not answer: a booking is not
        refused because a gate was unreachable.
    """
    result = parse_policy_validation(await validate_draft(draft))
    if result is None or result.valid:
        return None
    return result.customer_message or None


def _needs_sitting(draft: BookingDraft) -> bool:
    """Return ``True`` when the draft wants a sitting derived from its time.

    Requires a concrete ``starts_at`` (there is nothing to derive from
    otherwise) and an ABSENT ``service_id``.  A TriState counts as absent — the
    customer declined or did not know, which is precisely when deriving helps —
    but a concrete value never does: a customer who said "the lunch sitting"
    has chosen, and a probe that disagreed would silently overrule them.
    """
    request = draft.request
    starts_at = request.starts_at
    if starts_at is None or isinstance(starts_at, TriState):
        return False
    if request.operation not in {OperationType.create, OperationType.reschedule}:
        return False
    return request.service_id is None or isinstance(request.service_id, TriState)


async def capture_fields(state: AgentState) -> dict:
    """Extract and apply slot captures from the current transcript (§8.4).

    1. If ``state.draft`` is ``None``, nothing to capture — returns empty dict.
    2. Calls :func:`~cordia_agent.slots.capture.get_slot_extractor` and awaits
       its ``extract`` to obtain
       :class:`~cordia_agent.slots.capture.SlotCapture` objects from
       ``state.transcript_text``.  The port is async because the production
       extractor is model-backed.
    3. Applies captures via :func:`~cordia_agent.slots.capture.capture_slots`
       respecting ``state.transcript_is_final`` (Inv-1).
    4. Asks the tenant policy gate about the changed draft (§6.1) and records the
       venue's refusal, if any, on ``policy_block_message``.
    5. If the request changed (material update) AND ``tenant_id`` + ``session_id``
       are both set, checkpoints the updated state (§8.9).
    6. Returns the updated draft on state.

    Args:
        state: Current ``AgentState``.

    Returns:
        A partial state-update dict with the (possibly unchanged) ``draft``.
    """
    if state.draft is None:
        return {}

    extractor = get_slot_extractor()
    captures = await extractor.extract(state.transcript_text or "")

    # A spoken name ("dinner", "the Fitzroy room") is not an identifier and must
    # not become one by guesswork.  The resolver trades it for an id the tenant
    # has actually declared, and drops it when there is no unambiguous match —
    # so an unrecognised name leaves the field unset and clarify asks, rather
    # than a fabricated id reaching a booking payload.
    captures = await resolve_resource_names(captures)

    # "Seven pm" means seven pm WHERE THE VENUE IS.  The extractor reports the
    # spoken wall clock tagged UTC because a naive time has to serialise as
    # something; taking that tag literally booked 5am Melbourne for a caller who
    # said 7pm.  See slots/local_time.py.
    captures = [
        c.model_copy(
            update={"value": reanchor_to_venue(c.value, state.tenant_timezone)}
        )
        if c.field == "starts_at" and isinstance(c.value, datetime)
        else c
        for c in captures
    ]

    updated_draft = capture_slots(
        state.draft,
        captures,
        source_is_final=state.transcript_is_final,
        source_turn_id=state.current_turn_id,
    )

    # What the customer said this turn, before anything is derived from it.  The
    # policy gate is asked only when this is true: re-asking it every turn about
    # an unchanged draft buys a round trip of silence and the same answer.
    captured_something_new = (
        updated_draft.request.model_dump_json() != state.draft.request.model_dump_json()
    )

    # Policy BEFORE availability (§6.1).  A party of 400 or a 3am table is not a
    # capacity question, and asking the capacity question first meant the answer
    # to it — "which room would you like?" — was the last thing the caller heard.
    blocked: str | None = None
    if (
        state.transcript_is_final
        and captured_something_new
        and _has_policy_inputs(updated_draft)
    ):
        blocked = await _policy_block(updated_draft)

    # Nobody rings a restaurant asking for "the dinner sitting" — they ask for a
    # table at seven, and the venue's own capacity data already knows which
    # sitting that is.  Only when the customer has not named one themselves: an
    # explicit choice always wins over a derived one.  Skipped entirely once the
    # draft is blocked: there is no sitting to find for a time we are shut.
    if blocked is None and state.transcript_is_final and _needs_sitting(updated_draft):
        sitting = await derive_sitting(updated_draft)
        if sitting.outcome is SittingOutcome.derived and sitting.capture is not None:
            updated_draft = capture_slots(
                updated_draft,
                [sitting.capture],
                source_is_final=True,
                source_turn_id=state.current_turn_id,
            )
        elif sitting.outcome is SittingOutcome.outside_service:
            # The venue is open, but nothing is being served then.  Distinct from
            # the opening-hours refusal above, and distinct from a failed probe,
            # which says nothing and leaves clarify to ask as it always did.
            blocked = (
                NOT_SERVING_TEMPLATE.format(options=_spoken_list(sitting.service_names))
                if sitting.service_names
                else NOT_SERVING_UNKNOWN
            )

    # Detect material update by comparing request JSON (byte-stable).
    request_changed = (
        updated_draft.request.model_dump_json() != state.draft.request.model_dump_json()
    )

    # Did the customer give us anything new this turn?  clarify needs this to
    # tell "they answered something" from "they said nothing usable": a caller
    # who answers a DIFFERENT question is making progress, and escalating the
    # ladder at them is how "there'll be six of us" was answered with "tap the
    # date and time in on your keypad".
    # Always written, including as None: a refusal that outlived the turn that
    # earned it would have the agent repeat "we're closed then" at a caller who
    # has since said a time that works.
    result: dict = {
        "draft": updated_draft,
        "draft_advanced": request_changed,
        POLICY_BLOCK_KEY: blocked,
    }

    if request_changed and state.tenant_id and state.session_id:
        # Build the candidate updated state and checkpoint it.
        updated_state = state.model_copy(update={"draft": updated_draft})
        get_session_checkpointer().checkpoint_material_update(updated_state)

    return result


def route_after_capture_fields(state: AgentState) -> str:
    """Route after the capture loop based on draft completeness and operation (§8.4).

    Routing precedence:
    0. The draft breaks tenant policy → ``"clarify"``, which is where the venue's
       own words about it get said.
    1. No draft or required candidate fields missing → ``"clarify"``
    2. Mutation operation (modify/cancel/reschedule) without a verification
       grant → ``"verify_identity"``
    3. Create or reschedule with all required fields → ``"check_candidates"``
    4. Otherwise (modify/cancel with a grant, or any other path) → ``"read_back"``

    All four targets are enumerated in the ``path_map`` in ``graph.py`` so
    the compiled graph statically records every edge.

    Args:
        state: Current ``AgentState``.

    Returns:
        One of ``"clarify"``, ``"verify_identity"``, ``"check_candidates"``,
        ``"read_back"``.
    """
    # A draft the venue has already refused must not go on to be priced, held and
    # read back: every required field is present for "a table for 400 at 7pm", so
    # without this it routed to the availability check and on towards a booking.
    if policy_block_message(state):
        return "clarify"

    if state.draft is None or not required_candidate_fields_present(state.draft):
        return "clarify"

    # Route from the VALIDATED draft, not state.operation — the draft's request
    # is the authoritative captured state (Inv-1); the two can diverge and the
    # router must follow the draft.
    operation = state.draft.request.operation

    mutation_ops = {
        OperationType.modify,
        OperationType.cancel,
        OperationType.reschedule,
    }
    if operation in mutation_ops and state.verification_grant_id is None:
        return "verify_identity"

    candidate_ops = {OperationType.create, OperationType.reschedule}
    if operation in candidate_ops:
        return "check_candidates"

    return "read_back"
