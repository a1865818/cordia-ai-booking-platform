"""Node: clarify — §8.5 Clarification confidence/validation/escalation-ladder policy.

Asks the customer a targeted clarifying question when a captured field value is
ambiguous, missing, low-confidence, or invalid, then returns to capture_fields
to re-evaluate the updated draft (the clarify → capture_fields edge is the
ONLY edge out of clarify; see §8.3 topology).

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

from cordia_agent.slots.capture import (
    CAPTURABLE_FIELDS,
    REQUIRED_CANDIDATE_FIELDS,
)
from cordia_agent.state import FieldValidation, TriState


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

#: STT or model confidence at or below this value is considered "low confidence".
#: (§8.5: spec defers exact threshold to implementation; 0.5 chosen as policy.)
LOW_CONFIDENCE_THRESHOLD: float = 0.5


# ---------------------------------------------------------------------------
# Pure policy function
# ---------------------------------------------------------------------------


def decide_clarify_action(
    *,
    validation: FieldValidation | None,
    stt_confidence: float | None,
    model_confidence: float | None,
    attempt: int,
    is_datetime: bool,
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

    Returns:
        The :class:`ClarifyAction` for this turn.
    """
    _ladder_len = len(ESCALATION_LADDER)
    spell_back_rung = ESCALATION_LADDER.index(ClarifyAction.SPELL_BACK)

    if is_datetime and validation == FieldValidation.needs_clarification:
        # Ambiguous datetime: begin at SPELL_BACK, escalate by attempt.
        rung = max(spell_back_rung, min(attempt, _ladder_len - 1))
        return ESCALATION_LADDER[rung]

    # All other cases: escalate from rung 0 (ASK_AGAIN) by attempt.
    rung = min(attempt, _ladder_len - 1)
    return ESCALATION_LADDER[rung]


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
    # (a) Date portion — "Friday, 13 June 2026"
    date_str = dt.strftime("%A, %-d %B %Y")

    # (b) Local time — "7:00 PM"
    time_str = dt.strftime("%-I:%M %p")

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
         sentinel (missing/unknown/declined) for the current operation.
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
    for field_name in sorted(required):  # stable order
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
                f"I have {spell} — could you confirm that's correct, "
                f"or tell me the exact date and time you'd like?"
            )

    # Generic prompts for the remaining actions/fields.
    field_label = field.replace("_", " ")
    if action == ClarifyAction.ASK_AGAIN:
        return f"Could you please tell me the {field_label}?"
    if action == ClarifyAction.SPELL_BACK:
        return f"Could you spell out the {field_label} for me?"
    if action == ClarifyAction.DTMF:
        return (
            f"Please enter the {field_label} using your keypad, "
            f"followed by the hash key."
        )
    # ESCALATE_HANDOFF
    return (
        f"I'm having trouble capturing the {field_label}. "
        f"Let me connect you with a team member who can help."
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
    field = _pick_clarify_field(state)
    if field is None:
        # No field to clarify — return empty (no-op; routing logic should
        # prevent arriving here without a target, but be defensive).
        return {}

    attempt = state.clarification_attempts.get(field, 0)

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
        attempt=attempt,
        is_datetime=is_datetime,
    )

    prompt = _build_prompt(action, field, state)

    return {
        "clarification_attempts": {**state.clarification_attempts, field: attempt + 1},
        "pending_clarify_action": action.value,
        "pending_clarify_prompt": prompt,
        "pending_clarify_field": field,
    }
