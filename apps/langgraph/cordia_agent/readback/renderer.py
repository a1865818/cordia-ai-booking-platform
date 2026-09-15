"""Pure deterministic read-back renderer (spec 02 §8.6 / §8.8, Inv-16).

This module is PURE: no LLM, no I/O.  Given the same draft+mode it always
produces byte-identical text and read_back_version (sha256 content hash).
"""

from __future__ import annotations

import hashlib
import json
from datetime import datetime
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from pydantic import BaseModel

from cordia_agent.contracts.operation_status import NormalizedOperationStatus
from cordia_agent.state import BookingDraft, OperationType, TriState
from cordia_agent.tools.status_codes import AvailabilityMode


class ReadBackResult(BaseModel):
    """Result of a deterministic read-back render."""

    model_config = {"extra": "forbid"}

    text: str
    read_back_version: str


def _field_str(value: object) -> str:
    """Render a BookingRequest field value for human-readable output.

    TriState sentinels and None both produce explicit "not specified".
    """
    if value is None or isinstance(value, TriState):
        return "not specified"
    return str(value)


def _starts_at_str(draft: BookingDraft, timezone: str | None) -> str:
    """Render the start time on the VENUE's clock, which is the one the caller used.

    The draft stores an instant in UTC — that is what the capacity slot keys are
    written at, and it is the right thing to submit.  It is the wrong thing to
    say out loud: after "seven pm" is correctly anchored to Melbourne it becomes
    09:00Z, and a read-back that prints the stored value confirms nine in the
    morning to someone who asked for seven at night.
    """
    value = draft.request.starts_at
    if value is None or isinstance(value, TriState):
        return "not specified"
    if not isinstance(value, datetime) or not timezone:
        return str(value)
    try:
        local = value.astimezone(ZoneInfo(timezone))
    except (ZoneInfoNotFoundError, ValueError):
        return str(value)
    # "Wednesday 16 September at 7pm" — a date a person would say, not an
    # ISO-8601 stamp with an offset the caller cannot hear.  On the hour drops
    # the minutes entirely: nobody says "seven oh clock zero zero".
    hour = local.strftime("%I").lstrip("0") or "12"
    meridiem = local.strftime("%p").lower()
    clock = f"{hour}{meridiem}" if local.minute == 0 else f"{hour}:{local:%M}{meridiem}"
    return f"{local:%A %-d %B} at {clock}"


def _resource_str(draft: BookingDraft, field: str) -> str:
    """Render an identifier field as the name the customer would recognise.

    The identifier is what gets booked; it is not what anyone calls the thing.
    ``location_id='fitzroy'`` read aloud is a slug, and a caller asked to confirm
    "Location: fitzroy" has been shown a database row, not their reservation.
    When the slot resolver matched a declared resource it recorded that
    resource's name on ``draft.resolved_names``; prefer it, and fall back to the
    raw value when the customer spoke the identifier outright and nothing
    resolved it.
    """
    value = getattr(draft.request, field, None)
    if value is None or isinstance(value, TriState):
        return "not specified"
    return draft.resolved_names.get(field) or str(value)


def _closing_question(operation: OperationType, mode: AvailabilityMode) -> str:
    """The question that hands the turn back, chosen by what is being confirmed.

    Availability wording belongs to a booking.  Appended to a cancellation it
    produced "that's cancelling your booking. That looks free, though I can't
    hold it just yet" — a sentence about a slot nobody is trying to take.
    """
    if operation is OperationType.cancel:
        return "Shall I go ahead and cancel it?"
    return _mode_line(mode)


def _mode_line(mode: AvailabilityMode) -> str:
    """§6.5 availability wording, and the question that hands the turn back.

    Every one of these ends in a question, because a read-back IS a question —
    the customer is being asked to approve terms.  The old wording stated a fact
    and stopped, which on a call leaves a silence the customer has to guess the
    meaning of.

    The §6.5 distinction survives the rewrite: ``informational`` still says the
    slot is not held, because that caveat is the difference between a promise and
    an estimate (Inv-16).
    """
    if mode is AvailabilityMode.INFORMATIONAL:
        return "That looks free, though I can't hold it just yet. Shall I go ahead?"
    if mode is AvailabilityMode.LIVE:
        return "That's free right now. Shall I book it in for you?"
    if mode is AvailabilityMode.HELD:
        return "I can hold that while we finish up. Shall I?"
    # CONFIRMED is mapped for completeness; read_back never passes it in practice
    # (confirmed wording is post-booking only — Inv-16).
    return "You're all booked in."


#: Small numbers a person says as words.  "Party size: 4" is a form; "for four"
#: is a sentence, and a TTS voice reads the digit as a digit.
_SPOKEN_NUMBERS: dict[int, str] = {
    1: "one",
    2: "two",
    3: "three",
    4: "four",
    5: "five",
    6: "six",
    7: "seven",
    8: "eight",
    9: "nine",
    10: "ten",
    11: "eleven",
    12: "twelve",
}


def _spoken_count(value: object) -> str | None:
    """Render a party size as a person would say it, or ``None`` when absent."""
    if value is None or isinstance(value, TriState) or not isinstance(value, int):
        return None
    word = _SPOKEN_NUMBERS.get(value, str(value))
    return f"{word} {'person' if value == 1 else 'people'}"


def _said(label: str) -> bool:
    """Whether a rendered label is a real value rather than the absent sentinel."""
    return label != "not specified"


def _summary_sentence(
    draft: BookingDraft,
    service_label: str,
    location_label: str,
    starts_at_label: str,
) -> str:
    """Say the booking back the way a person taking it would.

    Absent fields are OMITTED rather than read out.  A human confirming a
    reservation does not say "duration, not specified" — they say nothing about
    the duration, and the customer hears a sentence instead of a form with holes
    in it.  The material terms are unchanged; only what is spoken aloud differs,
    and the hash below still covers every one of them.
    """
    operation = draft.request.operation
    party = _spoken_count(draft.request.party_size)

    # Labels are NOT lower-cased: they are the venue's own proper nouns, and
    # "the fitzroy dining room" reads as a typo out loud.
    clauses: list[str] = []
    if _said(service_label):
        clauses.append(f"the {service_label}")
    if party:
        clauses.append(f"for {party}")
    if _said(location_label):
        clauses.append(f"in the {location_label}")
    if _said(starts_at_label):
        clauses.append(f"on {starts_at_label}")

    detail = ", ".join(clauses)

    if operation is OperationType.cancel:
        # A cancellation draft carries the target id, not the table's details,
        # so identify_intent leaves the words for it on resolved_names.
        target = draft.resolved_names.get("target_booking_id")
        if target:
            return f"Right, so that's cancelling {target}."
        return (
            f"Right, so that's cancelling {detail}."
            if detail
            else "Right, so that's cancelling your booking."
        )
    if operation is OperationType.reschedule:
        return (
            f"Right, so I'll move you to {detail}."
            if detail
            else "Right, so I'll move that for you."
        )
    if operation is OperationType.modify:
        return (
            f"Right, so I'll change that to {detail}."
            if detail
            else "Right, so I'll update that for you."
        )
    return (
        f"Right, so that's {detail}." if detail else "Right, let me take those details."
    )


def render_read_back(
    draft: BookingDraft, mode: AvailabilityMode, timezone: str | None = None
) -> ReadBackResult:
    """Render a deterministic human-readable summary of the booking draft.

    Always produces the same ``text`` and ``read_back_version`` for the same
    ``draft`` + ``mode`` pair (Inv-16, §8.6).

    Args:
        draft: The in-dialog booking draft.
        mode: Current availability mode (controls the §6.5 wording line).
        timezone: The venue's IANA zone.  The draft holds UTC; the caller hears
            their own clock.  ``None`` falls back to printing the stored value.

    Returns:
        A ``ReadBackResult`` with ``text`` and ``read_back_version`` (sha256
        content hash over material terms + mode.value).
    """
    req = draft.request

    # Extract material terms (the set that defines a unique booking)
    operation = req.operation.value
    service_id = _field_str(req.service_id)
    location_id = _field_str(req.location_id)
    service_label = _resource_str(draft, "service_id")
    location_label = _resource_str(draft, "location_id")
    starts_at = _field_str(req.starts_at)
    starts_at_label = _starts_at_str(draft, timezone)
    party_size = _field_str(req.party_size)
    duration_minutes = _field_str(req.duration_minutes)

    # A FIXED template, still — but a spoken one.  Determinism was never what made
    # this sound like a database row: "Operation: create / Service: Dinner sitting
    # / Party size: 4" is byte-identical for a given draft, and so is a sentence.
    # The old form read a schema aloud, including field names no caller has words
    # for ("operation") and absent values they should never hear ("Duration: not
    # specified").  Same inputs, same bytes, same hash — different human being.
    text = (
        f"{_summary_sentence(draft, service_label, location_label, starts_at_label)} "
        f"{_closing_question(req.operation, mode)}"
    )

    # Content hash over canonical JSON of material terms + mode (Inv-16, §8.6)
    # The hash covers the identifiers AND the labels actually spoken.  Consent is
    # recorded against read_back_version, so it has to cover what the customer
    # HEARD: if a tenant renames a room between two renders of the same ids, the
    # customer was read two different things and must not be held to one version.
    canonical: dict[str, str] = {
        "operation": operation,
        "service_id": service_id,
        "location_id": location_id,
        "service_label": service_label,
        "location_label": location_label,
        "starts_at_label": starts_at_label,
        "target_label": draft.resolved_names.get("target_booking_id", ""),
        "starts_at": starts_at,
        "party_size": party_size,
        "duration_minutes": duration_minutes,
        "mode": mode.value,
    }
    canonical_json = json.dumps(canonical, sort_keys=True, separators=(",", ":"))
    read_back_version = hashlib.sha256(canonical_json.encode()).hexdigest()

    return ReadBackResult(text=text, read_back_version=read_back_version)


def status_wording(status: NormalizedOperationStatus) -> str:
    """Map a NormalizedOperationStatus to safe customer-facing wording (Inv-16).

    ONLY ``booked`` yields "confirmed/booked" language.  All other statuses
    produce submitted/processing-style wording and must NEVER include the word
    "confirmed" (Inv-16).

    The wording is warm but the invariant is unchanged, and it constrains the
    warmth: a reassuring phrase like "that's all confirmed" is exactly the thing
    Inv-16 forbids on anything short of ``booked``, because a customer who hears
    it will turn up.  So the non-booked lines say what is true — it went through,
    it is being worked on — and promise nothing.
    """
    if status is NormalizedOperationStatus.BOOKED:
        return "Lovely, you're all booked in. We'll see you then!"
    if status is NormalizedOperationStatus.ACCEPTED:
        return "Got it, that's gone through. I'll have it sorted in just a moment."
    if status is NormalizedOperationStatus.IN_PROGRESS:
        return "I'm just sorting that out now."
    if status is NormalizedOperationStatus.AWAITING_USER_ACTION:
        return "There's one more thing I'll need from you before I can finish that."
    if status is NormalizedOperationStatus.ALTERNATIVES_REQUIRED:
        return "That one didn't work out, sorry. Let's find you another option."
    if status is NormalizedOperationStatus.WAITLISTED:
        return "I've popped you on the waitlist and we'll be in touch if something opens up."
    if status is NormalizedOperationStatus.REJECTED:
        return "I'm sorry, we weren't able to make that one work."
    if status is NormalizedOperationStatus.EXPIRED:
        return "That one's expired, I'm afraid. Shall we start it again?"
    # FAILED
    return "Sorry, something went wrong on our end. Could we try that again?"
