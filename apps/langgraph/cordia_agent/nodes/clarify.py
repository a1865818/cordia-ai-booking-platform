"""Node: clarify — §8.5 Clarification confidence/validation/escalation-ladder policy.

Asks the customer a targeted clarifying question when a captured field value is
ambiguous, missing, low-confidence, or invalid — or when availability came back
empty and the only thing that can unblock the booking is a different time.

Clarify ENDs the turn.  It is the point of the graph at which the agent has
said something and can do nothing further until the customer answers, and the
edge back to ``capture_fields`` that the original §8.3 topology drew was a
cycle rather than a recovery: re-entering capture_fields re-ran the extractor
over the SAME transcript, produced the same draft, and returned here until the
recursion limit fired.  See ``graph.py`` on the per-turn boundary.

Policy summary (§8.5):
  - A 4-rung escalation ladder: ASK_AGAIN → SPELL_BACK → DTMF → ESCALATE_HANDOFF.
  - Ambiguous datetime fields (``starts_at`` with
    ``FieldValidation.needs_clarification``) start at SPELL_BACK and escalate.
  - All other cases escalate from ASK_AGAIN on attempt 0.
  - ESCALATE_HANDOFF is a state signal (``pending_clarify_action``); it does NOT
    add a graph edge.  Graph-level routing from a clarify dead-end to
    human_handoff is a spec gap deferred beyond this ticket (§8.3 frozen topology).

Invariants:
  - Inv-1: clarify NEVER writes draft fields.  Re-capture is delegated entirely
    to capture_fields via the slot engine.
"""

from __future__ import annotations

from enum import Enum
from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from pydantic import AwareDatetime

    from cordia_agent.state import AgentState

from cordia_agent.readback.renderer import join_with_conjunction
from cordia_agent.slots.capture import (
    CAPTURABLE_FIELDS,
    REQUIRED_CANDIDATE_FIELDS,
)
from cordia_agent.state import FieldValidation, TriState
from cordia_agent.tools.client_binding import (
    parse_resolved_resources,
    resolve_tenant_resources_call,
)


# ---------------------------------------------------------------------------
# ClarifyAction enum and escalation ladder
# ---------------------------------------------------------------------------


class ClarifyAction(str, Enum):
    """The four clarification actions in escalation order (§8.5).

    The rungs are:
      0. ASK_AGAIN      — re-ask the question in natural language.
      1. SPELL_BACK     — spell back the captured datetime and ask for correction.
      2. DTMF           — ask the customer to enter the value via keypad.
      3. ESCALATE_HANDOFF — escalate to a human agent.

    ESCALATE_HANDOFF is a policy decision surfaced on AgentState; it does NOT
    add a clarify → human_handoff graph edge (§8.3 topology frozen by 02-C).
    """

    ASK_AGAIN = "ask_again"
    SPELL_BACK = "spell_back"
    DTMF = "dtmf"
    ESCALATE_HANDOFF = "escalate_handoff"
    #: Read the tenant's declared options aloud and ask the caller to pick.  For a
    #: field whose legal values are a closed set the tenant has published, an open
    #: question ("could you tell me the service?") asks the caller to guess at a
    #: vocabulary only the venue knows.
    OFFER_OPTIONS = "offer_options"


#: The escalation ladder in order.  Indexes are used directly by
#: :func:`decide_clarify_action` to compute the rung.
ESCALATION_LADDER: tuple[ClarifyAction, ...] = (
    ClarifyAction.ASK_AGAIN,
    ClarifyAction.SPELL_BACK,
    ClarifyAction.DTMF,
    ClarifyAction.ESCALATE_HANDOFF,
)

# ---------------------------------------------------------------------------
# Thresholds (§8.5 — spec leaves exact numbers open; documented as policy)
# ---------------------------------------------------------------------------

#: Fields whose value must be one of the tenant's declared resources, mapped to
#: the ``resolve_tenant_resources`` kind that lists them.  These are the fields
#: the agent can enumerate instead of interrogate.
RESOURCE_FIELD_KINDS: dict[str, str] = {
    "service_id": "service",
    "location_id": "location",
}

#: Fields a telephone keypad can actually express.  The DTMF rung exists for
#: digits — a booking reference, a party size.  It was being offered for
#: ``service_id`` too, which asks the caller to type "dinner" on a number pad.
#: Any field not listed here skips the DTMF rung entirely.
#:
#: ``starts_at`` is NOT here.  It is numeric in principle and unusable in
#: practice: "could you tap the date and time in on your keypad" is not a thing
#: anyone has ever said to someone booking a table, and there is no format the
#: caller could guess.  Asking again in words is the only sane rung.
KEYPAD_FIELDS: frozenset[str] = frozenset(
    {
        "party_size",
        "duration_minutes",
        "target_booking_id",
        "target_booking_version",
    }
)

#: Phrases that mean "I don't know the vocabulary, tell me the options".  A caller
#: saying this has NOT failed to answer — they have asked a question, and the
#: escalation ladder must not charge them an attempt for it.  Escalating here is
#: how "what kind of service do you have?" ends up at "enter it on your keypad".
OPTIONS_REQUEST_PHRASES: tuple[str, ...] = (
    "what kind",
    "what sort",
    "what type",
    "what do you have",
    "what have you got",
    "what are my options",
    "what options",
    "which ones",
    "list",
    "options",
    "choices",
    "don't know",
    "dont know",
    "no idea",
    "not sure",
    "like what",
)

#: At most this many options are read aloud before the agent stops listing.
MAX_SPOKEN_OPTIONS: int = 4

#: Draft field name -> the noun phrase a caller would recognise, slotted into the
#: prompt templates below.  Without this the templates fall back to the field name
#: with underscores stripped, and the caller is asked for "the location id" — a
#: column name read down a phone line.  Every entry has to survive "tell me the
#: {label}", "spell out the {label}" and "enter the {label} using your keypad",
#: so they are all bare noun phrases.
FIELD_LABELS: dict[str, str] = {
    "service_id": "service",
    "location_id": "location",
    "resource_id": "resource",
    "starts_at": "date and time",
    "party_size": "number of people",
    "duration_minutes": "duration",
    "special_requests": "special requests",
    "target_booking_id": "booking reference",
    "target_booking_version": "booking version",
}

#: The order required fields are asked in when several are missing.
#:
#: Alphabetical order put ``location_id`` first, so the agent opened by asking
#: which room the caller wanted before it knew when they were coming.  Asking for
#: the SERVICE first was only marginally better and became actively wrong once the
#: sitting was derived from the time: "I'd like to make a reservation" was answered
#: with "Breakfast, Lunch sitting, or Dinner sitting?", a question the agent would
#: not have needed to ask had it asked *when* first and worked the sitting out.
#:
#: So: when, how many, where — and the service LAST, because by the time a caller
#: has given a time it is usually no longer a question at all.
FIELD_ASK_ORDER: tuple[str, ...] = (
    "starts_at",
    "party_size",
    "location_id",
    "service_id",
)


#: What a person would actually ASK for each field, rather than one generic
#: frame with a label dropped in.  "Could you please tell me the party size?" is
#: a form being read aloud; "How many of you will there be?" is a question.
#: Unlisted fields fall back to the generic frame.
ASK_PROMPTS: dict[str, str] = {
    "starts_at": "Sure! What day and time were you thinking?",
    "party_size": "Lovely. And how many of you will there be?",
    "location_id": "And whereabouts would you like to sit?",
    "service_id": "And what were you after?",
    "duration_minutes": "Roughly how long do you think you'll need?",
    "target_booking_id": "No problem. Do you have the booking reference handy?",
}


def field_label(field: str) -> str:
    """Return the customer-facing noun phrase for *field*.

    Falls back to the field name with underscores replaced, which is wrong for
    any ``*_id`` field and right enough for anything unmapped.
    """
    return FIELD_LABELS.get(field, field.replace("_", " "))


def _ask_order(field: str) -> tuple[int, str]:
    """Sort key placing *field* in :data:`FIELD_ASK_ORDER`, unlisted fields last."""
    try:
        return (FIELD_ASK_ORDER.index(field), field)
    except ValueError:
        return (len(FIELD_ASK_ORDER), field)


#: STT or model confidence at or below this value is considered "low confidence".
#: (§8.5: spec defers exact threshold to implementation; 0.5 chosen as policy.)
LOW_CONFIDENCE_THRESHOLD: float = 0.5


# ---------------------------------------------------------------------------
# Pure policy function
# ---------------------------------------------------------------------------


def ladder_for(field: str | None) -> tuple[ClarifyAction, ...]:
    """Return the escalation ladder appropriate to *field*.

    Three ladders, because one ladder cannot fit three kinds of field:

    - **A resource field** (service, location) has a closed set of legal values
      the tenant has published.  Offering them is the whole job; spelling and
      keypads are both answers to a question the caller never had.  Two offers,
      then a human.
    - **A keypad-expressible field** keeps the original four rungs.
    - **Anything else** keeps the ladder minus DTMF, which it cannot use.

    Args:
        field: Draft field name, or ``None`` for the generic ladder.

    Returns:
        The ladder to index by attempt.
    """
    if field is None:
        return ESCALATION_LADDER
    if field in RESOURCE_FIELD_KINDS:
        return (
            ClarifyAction.OFFER_OPTIONS,
            ClarifyAction.OFFER_OPTIONS,
            ClarifyAction.ESCALATE_HANDOFF,
        )
    if field in KEYPAD_FIELDS:
        return ESCALATION_LADDER
    return tuple(a for a in ESCALATION_LADDER if a is not ClarifyAction.DTMF)


def is_options_request(text: str) -> bool:
    """Return ``True`` when *text* is asking what the choices are.

    Used to decide whether a turn counts as a clarification ATTEMPT.  It should
    not: the caller who says "what kind of service do you have?" has answered
    nothing, but they have also failed at nothing, and charging them a rung is
    what turns a reasonable question into "enter it on your keypad".
    """
    lower = text.lower()
    return any(phrase in lower for phrase in OPTIONS_REQUEST_PHRASES)


def decide_clarify_action(
    *,
    validation: FieldValidation | None,
    stt_confidence: float | None,
    model_confidence: float | None,
    attempt: int,
    is_datetime: bool,
    field: str | None = None,
) -> ClarifyAction:
    """Return the clarification action for the given signals and attempt count (§8.5).

    Precedence:
      1. Ambiguous datetime (``is_datetime=True`` AND
         ``validation == FieldValidation.needs_clarification``):
         start at SPELL_BACK (rung 1) and escalate by attempt.
         ``rung = max(LADDER.index(SPELL_BACK), min(attempt, len-1))``.
      2. All other cases (missing, low-confidence, invalid):
         escalate purely by attempt from ASK_AGAIN (rung 0).
         ``rung = min(attempt, len(LADDER) - 1)``.
         Attempt 0 → ASK_AGAIN, 1 → SPELL_BACK, 2 → DTMF, 3+ → ESCALATE_HANDOFF.

    Args:
        validation: Validation outcome from :class:`~cordia_agent.state.FieldProvenance`,
            or ``None`` if no provenance recorded yet.
        stt_confidence: STT/ASR confidence (0.0-1.0), or ``None``.
        model_confidence: Model confidence (0.0-1.0), or ``None``.
        attempt: Number of clarification attempts made so far for this field.
        is_datetime: ``True`` when the target field is a datetime (``starts_at``).
        field: The draft field being clarified.  Selects the ladder: a resource
            field is enumerated rather than interrogated, and a field a keypad
            cannot express never reaches the DTMF rung.  ``None`` keeps the
            original generic ladder.

    Returns:
        The :class:`ClarifyAction` for this turn.
    """
    ladder = ladder_for(field)
    _ladder_len = len(ladder)

    if is_datetime and validation == FieldValidation.needs_clarification:
        # Ambiguous datetime: begin at SPELL_BACK, escalate by attempt.
        spell_back_rung = ladder.index(ClarifyAction.SPELL_BACK)
        rung = max(spell_back_rung, min(attempt, _ladder_len - 1))
        return ladder[rung]

    # All other cases: escalate from rung 0 by attempt.
    rung = min(attempt, _ladder_len - 1)
    return ladder[rung]


# ---------------------------------------------------------------------------
# Datetime spell-back renderer
# ---------------------------------------------------------------------------


def render_datetime_spell_back(
    dt: AwareDatetime,
    *,
    tz_label: str | None = None,
) -> str:
    """Render an aware datetime as a human-readable spell-back string (§8.5).

    The string deterministically contains ALL THREE of:
      (a) the DATE   — e.g. "Friday, 13 June 2026"
      (b) the LOCAL TIME — e.g. "7:00 PM"
      (c) the TIMEZONE  — ``tz_label`` if given, else the UTC offset like "UTC+10:00"

    Args:
        dt: A timezone-aware :class:`~pydantic.AwareDatetime`.
        tz_label: Optional human-readable timezone label (e.g. ``"AEST"``).
            When omitted the UTC offset is used (e.g. ``"UTC+10:00"``).

    Returns:
        A deterministic spell-back string containing date, local time, and timezone.
    """
    # (a) Date portion — "Friday, 13 June 2026" (no %-d: not portable to Windows)
    date_str = dt.strftime(f"%A, {dt.day} %B %Y")

    # (b) Local time — "7:00 PM" (no %-I: not portable to Windows)
    hour_12 = dt.hour % 12 or 12
    time_str = dt.strftime(f"{hour_12}:%M %p")

    # (c) Timezone
    if tz_label:
        tz_str = tz_label
    else:
        offset = dt.utcoffset()
        if offset is None:
            tz_str = "UTC"
        else:
            total_seconds = int(offset.total_seconds())
            sign = "+" if total_seconds >= 0 else "-"
            total_seconds = abs(total_seconds)
            hours, remainder = divmod(total_seconds, 3600)
            minutes = remainder // 60
            tz_str = f"UTC{sign}{hours:02d}:{minutes:02d}"

    return f"{date_str} at {time_str} ({tz_str})"


# ---------------------------------------------------------------------------
# Field selection helpers
# ---------------------------------------------------------------------------


def _pick_clarify_field(state: AgentState) -> str | None:
    """Select the field most in need of clarification from *state* (§8.5).

    Selection order (first match wins):
      1. First required-candidate field that is ``None`` or a :class:`TriState`
         sentinel (missing/unknown/declined) for the current operation, taken in
         :data:`FIELD_ASK_ORDER` rather than alphabetically.
      2. First field whose provenance records ``validation=needs_clarification``.
      3. First captured field whose STT or model confidence is at or below
         :data:`LOW_CONFIDENCE_THRESHOLD`.
      4. Any capturable field that is ``None`` (fallback for optional missing fields).

    Returns ``None`` when no field needs clarification (should not happen in
    practice because :func:`route_after_capture_fields` only routes here when a
    field is genuinely missing/ambiguous).
    """
    if state.draft is None:
        return None

    request = state.draft.request
    provenance = state.draft.field_provenance

    # 1. Required-candidate fields that are missing/TriState.
    required = REQUIRED_CANDIDATE_FIELDS.get(request.operation, frozenset())
    for field_name in sorted(required, key=_ask_order):  # conversational order
        value = getattr(request, field_name, None)
        if value is None or isinstance(value, TriState):
            return field_name

    # 2. Fields with needs_clarification provenance.
    for field_name in sorted(provenance):
        prov = provenance[field_name]
        if prov.validation == FieldValidation.needs_clarification:
            return field_name

    # 3. Low-confidence captured fields.
    for field_name in sorted(provenance):
        prov = provenance[field_name]
        stt = prov.stt_confidence
        model = prov.model_confidence
        if (stt is not None and stt <= LOW_CONFIDENCE_THRESHOLD) or (
            model is not None and model <= LOW_CONFIDENCE_THRESHOLD
        ):
            return field_name

    # 4. Any capturable field that is still None (optional fields not yet provided).
    for field_name in sorted(CAPTURABLE_FIELDS):
        value = getattr(request, field_name, None)
        if value is None:
            return field_name

    return None


# ---------------------------------------------------------------------------
# Prompt builders
# ---------------------------------------------------------------------------


def _no_availability_prompt(state: AgentState) -> str | None:
    """Return the "that time is full" question, when that is what happened.

    ``route_after_check_candidates`` sends a genuinely full slot here, and the
    generic field prompts are all wrong for it: every required field is present
    and correct, so "could you tell me the party size?" re-asks for something
    the customer already said.  What is needed is a different TIME.

    Returns ``None`` unless availability has actually been checked and came
    back empty — an unchecked draft is a missing-field case, not a full one.
    """
    if not state.availability_checked or state.availability_candidates:
        return None
    if state.availability_invalid_fields:
        # The lookup rejected a field rather than finding the slot full; the
        # field-specific prompts below are the right answer.
        return None

    starts_at = state.draft.request.starts_at if state.draft else None
    if starts_at is not None and not isinstance(starts_at, TriState):
        spell = render_datetime_spell_back(starts_at)
        return (
            f"Ah, we're fully booked at {spell}, sorry. "
            "Would a bit earlier or later work, or another day?"
        )
    return (
        "Ah, we're fully booked then, sorry. "
        "Would another time work, or a different day?"
    )


async def _offer_options_prompt(field: str) -> str | None:
    """Ask the caller to pick from the tenant's DECLARED options for *field*.

    Calls ``resolve_tenant_resources`` with no query, which is the documented
    "listed" mode and the only way to name options without inventing any.

    Returns:
        The question, or ``None`` when the lookup failed or the tenant has
        declared nothing — in which case the caller falls back to the open
        question.  Naming a service the tenant has not declared would be the
        same fabrication the resolver exists to prevent.
    """
    kind = RESOURCE_FIELD_KINDS[field]
    view = parse_resolved_resources(await resolve_tenant_resources_call(kind=kind))
    if view is None or not view.matches:
        return None

    names = [m.name for m in view.matches[:MAX_SPOKEN_OPTIONS]]
    listed = join_with_conjunction(names, conjunction="or")

    tail = (
        " Those are the popular ones, anyway."
        if len(view.matches) > MAX_SPOKEN_OPTIONS
        else ""
    )
    return f"We've got {listed}. Which would you prefer?{tail}"


def _build_prompt(
    action: ClarifyAction,
    field: str,
    state: AgentState,
) -> str:
    """Build the clarifying-question prompt string for *action* and *field*.

    Uses :func:`render_datetime_spell_back` when ``action`` is ``SPELL_BACK``
    and ``field`` is ``starts_at`` and the draft holds a concrete datetime.
    """
    if (
        action == ClarifyAction.SPELL_BACK
        and field == "starts_at"
        and state.draft is not None
    ):
        starts_at = state.draft.request.starts_at
        if starts_at is not None and not isinstance(starts_at, TriState):
            spell = render_datetime_spell_back(starts_at)
            return (
                f"I've got {spell}. Is that right, or did you mean "
                f"a different day or time?"
            )

    # Generic prompts for the remaining actions/fields.
    if action in {ClarifyAction.ASK_AGAIN, ClarifyAction.OFFER_OPTIONS}:
        # OFFER_OPTIONS only reaches here when the listing lookup failed: ask
        # openly rather than inventing options.
        return ASK_PROMPTS.get(field, f"Could you tell me the {field_label(field)}?")
    label = field_label(field)
    if action == ClarifyAction.SPELL_BACK:
        if field == "starts_at":
            # The datetime SPELL_BACK above reads a captured value back. Reaching
            # here means there is nothing to read back, and "could you spell the
            # date and time out for me" is not a question anyone can answer.
            return (
                "Sorry, I didn't catch that — what day were you after, and what time?"
            )
        return f"Sorry, could you spell the {label} out for me?"
    if action == ClarifyAction.DTMF:
        return f"Could you tap the {label} in on your keypad, then press hash?"
    # ESCALATE_HANDOFF
    return (
        f"I'm having a bit of trouble getting the {label}, sorry. "
        f"Let me pass you to someone who can help."
    )


# ---------------------------------------------------------------------------
# The clarify node
# ---------------------------------------------------------------------------


async def clarify(state: AgentState) -> dict:
    """Issue a clarifying question for an ambiguous/missing field (§8.5).

    1. Selects the field needing clarification via :func:`_pick_clarify_field`.
    2. Reads the current attempt count from ``state.clarification_attempts``.
    3. Calls :func:`decide_clarify_action` with the field's provenance signals.
    4. Builds the prompt (spell-back for ambiguous datetime; simple ask otherwise).
    5. Returns the 4 internal clarification channels incremented/set.

    **Invariant Inv-1**: this node NEVER writes any field on ``state.draft``.
    Re-capture is fully delegated to ``capture_fields`` via the slot engine.
    The node returns an empty dict when no field is found (should be rare).

    Args:
        state: Current :class:`~cordia_agent.state.AgentState`.

    Returns:
        Partial state-update dict with updated internal clarification channels.
        Draft is never included in the return value (Inv-1).
    """
    # A full slot reaches clarify with a COMPLETE draft, so the field-picking
    # ladder below has nothing to legitimately ask about.  Handle it first.
    # The venue has already said no to this draft.  Say THAT rather than asking
    # the next form field: "we're closed Mondays" is the answer to a Monday
    # booking, and "whereabouts would you like to sit?" is not.  Charged to
    # starts_at, matching the no-availability convention below, since a refusal
    # is almost always about when.
    if state.policy_block_message:
        attempts = state.clarification_attempts.get("starts_at", 0)
        return {
            "clarification_attempts": {
                **state.clarification_attempts,
                "starts_at": attempts + 1,
            },
            "pending_clarify_action": ClarifyAction.ASK_AGAIN.value,
            "pending_clarify_prompt": state.policy_block_message,
            "pending_clarify_field": "starts_at",
        }

    no_availability = _no_availability_prompt(state)
    if no_availability is not None:
        attempts = state.clarification_attempts.get("starts_at", 0)
        return {
            "clarification_attempts": {
                **state.clarification_attempts,
                "starts_at": attempts + 1,
            },
            "pending_clarify_action": ClarifyAction.ASK_AGAIN.value,
            "pending_clarify_prompt": no_availability,
            "pending_clarify_field": "starts_at",
        }

    field = _pick_clarify_field(state)
    if field is None:
        # No field to clarify — return empty (no-op; routing logic should
        # prevent arriving here without a target, but be defensive).
        return {}

    attempt = state.clarification_attempts.get(field, 0)

    # A question is not a failed attempt.  "What kind of service do you have?"
    # answers nothing, but it fails at nothing either, and charging it a rung is
    # exactly how a reasonable question escalated to "enter it on your keypad".
    asked_for_options = is_options_request(state.transcript_text or "")

    # Progress is not failure.  A caller who answers a different question than
    # the one asked has still told us something, and the ladder must not climb
    # at them — "there'll be six of us" being met with "tap the date and time in
    # on your keypad" is the shape of an agent that is counting, not listening.
    made_progress = state.draft_advanced

    # Gather provenance signals for the chosen field.
    prov = state.draft.field_provenance.get(field) if state.draft else None
    validation = prov.validation if prov else None
    stt_confidence = prov.stt_confidence if prov else None
    model_confidence = prov.model_confidence if prov else None
    is_datetime = field == "starts_at"

    action = decide_clarify_action(
        validation=validation,
        stt_confidence=stt_confidence,
        model_confidence=model_confidence,
        # Ask plainly again when they gave us something: the rung reflects how
        # many times WE have failed to get this field, not how long the call is.
        attempt=0 if (made_progress or asked_for_options) else attempt,
        is_datetime=is_datetime,
        field=field,
    )

    prompt: str | None = None
    if action is ClarifyAction.OFFER_OPTIONS:
        prompt = await _offer_options_prompt(field)
        if prompt is None:
            # Nothing declared, or the lookup failed.  Ask openly rather than
            # name an option the tenant never published.
            action = ClarifyAction.ASK_AGAIN
    if prompt is None:
        prompt = _build_prompt(action, field, state)

    next_attempt = attempt if (asked_for_options or made_progress) else attempt + 1

    return {
        "clarification_attempts": {
            **state.clarification_attempts,
            field: next_attempt,
        },
        "pending_clarify_action": action.value,
        "pending_clarify_prompt": prompt,
        "pending_clarify_field": field,
    }
