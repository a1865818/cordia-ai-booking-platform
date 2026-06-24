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

from pydantic import ValidationError

from cordia_agent.state import AgentState, DialogIntent, OperationType


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


async def identify_intent(state: AgentState) -> dict:
    """Classify ``state.transcript_text`` and update intent + operation on state (§8.3).

    Sets ``dialog_intent`` on every call.  When the intent maps to a booking
    operation, also sets ``operation`` and, if a draft is active, updates
    ``draft.request.operation`` to match.

    Args:
        state: Current ``AgentState``.

    Returns:
        A partial state-update dict with ``dialog_intent`` and, for booking
        intents, ``operation`` (and optionally ``draft``).
    """
    text = state.transcript_text or ""
    intent = classify_intent(text)
    update: dict = {"dialog_intent": intent}

    operation = _INTENT_TO_OPERATION.get(intent)
    if operation is not None:
        update["operation"] = operation

        # Propagate operation to the draft's request if a draft is already active.
        if state.draft is not None:
            try:
                new_request = state.draft.request.model_copy(
                    update={"operation": operation}
                )
                update["draft"] = state.draft.model_copy(
                    update={"request": new_request}
                )
            except ValidationError:
                # If revalidation rejects the update (e.g. a mutation operation
                # whose target_booking_id is still missing), leave the draft
                # untouched — IdentifyIntent only sets the intent, not slots.
                pass

    return update
