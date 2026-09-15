"""Node: verify_identity — §8.6 Identity Verification / §9.3 Grant Gate (Inv-11).

Obtains a platform-issued verification grant before any mutation can reach
SubmitIntent.  Caller ID alone NEVER suffices (Inv-11); a valid, in-scope,
unexpired platform grant is required.

Grant validation outcomes:
  VALID   → sets verification_grant_id, routes back to capture_fields (advance).
  EXPIRED → clears verification_grant_id, routes back to capture_fields (re-verify).
  INVALID → sets terminal_state="human_handoff", routes to human_handoff (Inv-11).

This node performs NO mutation and NEVER mints/advances operation_id (02-J owns that).

Pass-through guard: if the operation is not a mutation (create/inquire) this node
should not have been entered; we treat it as a pass-through that routes back to
capture_fields without a grant — documented deliberately so the router is
deterministic even on an unexpected entry.

The multi-turn code exchange
    Verification takes at least two turns, because a code has to travel to the
    caller's phone and back through their mouth.  This node used to implement
    only the second half: it READ ``state.verification_id`` and nothing in the
    graph ever assigned it, so every mutation logged "called without
    verification_id; treating as invalid" and went to human_handoff.  The flow
    now is:

    1. First entry, no ``verification_id`` — call ``request_verification`` with
       an idempotency key derived from the session, the action and the attempt
       round (Inv-9: a replayed turn returns ``already_sent`` instead of
       sending a second code), say so in ``pending_verification_prompt``, and
       END the turn (graph.py, per-turn boundary).
    2. Next turn — read the digits out of the transcript and submit them with
       ``check_verification``.  A valid grant advances to capture_fields; a
       wrong or unheard code re-asks, bounded, then hands off.

    Caller ID is never any part of this (Inv-11, spec 08 §6): the caller's own
    number can find their booking, and it still cannot authorize a change to it.
"""

from __future__ import annotations

import logging
import re
from datetime import UTC, datetime
from enum import Enum
from typing import TYPE_CHECKING

from langgraph.graph import END

from cordia_agent.state import AgentState, OperationType
from cordia_agent.tools.client_binding import (
    check_verification_call,
    parse_verification_grant,
    parse_verification_sent,
    request_verification_call,
)
from cordia_agent.tools.contracts import VerificationRequest
from cordia_agent.tools.status_codes import VerificationAction


if TYPE_CHECKING:
    from cordia_agent.tools.contracts import VerificationGrantView


_log = logging.getLogger(__name__)


class GrantOutcome(str, Enum):
    """Outcome of validating a platform-issued verification grant (02-K, Inv-11)."""

    VALID = "valid"
    EXPIRED = "expired"
    INVALID = "invalid"


def _required_action(operation: OperationType | None) -> VerificationAction | None:
    """Map a booking OperationType to the required VerificationAction.

    Returns:
        The ``VerificationAction`` required for the operation, or ``None`` when
        the operation does not require a verification grant (create / inquire /
        unknown — pass-through cases).

    Spec:
        reschedule is a modify operation — both require MODIFY (§9.3).
    """
    if operation is OperationType.modify or operation is OperationType.reschedule:
        return VerificationAction.MODIFY
    if operation is OperationType.cancel:
        return VerificationAction.CANCEL
    # create / inquire / None → no verification required
    return None


def validate_grant(
    grant: VerificationGrantView | None,
    *,
    tenant_id: str | None,
    action: VerificationAction,
    now: datetime,
) -> GrantOutcome:
    """Validate a platform-issued verification grant against expected scope.

    Inv-11: caller ID alone is NEVER sufficient — a grant_id must be present.

    Args:
        grant: The ``VerificationGrantView`` from check_verification, or ``None``
            if the tool returned a non-ok envelope / pending / failed status.
        tenant_id: The session tenant ID to match against.
        action: The ``VerificationAction`` the grant must permit.
        now: The current UTC datetime (injected for deterministic tests).

    Returns:
        ``VALID``   when the grant is present, in-scope, and unexpired.
        ``EXPIRED`` when the grant is otherwise valid but has expired.
        ``INVALID`` when absent, missing grant_id, or out of scope.
    """
    # None grant or absent grant_id → no platform grant (Inv-11)
    if grant is None or grant.grant_id is None:
        return GrantOutcome.INVALID

    # Wrong tenant scope or wrong action → forged / mismatched grant (Inv-11)
    if grant.tenant_id != tenant_id or grant.allowed_action != action:
        return GrantOutcome.INVALID

    # Expiry check — strict (<=) so an exactly-expired grant is rejected
    if grant.expires_at <= now:
        return GrantOutcome.EXPIRED

    return GrantOutcome.VALID


# ---------------------------------------------------------------------------
# Spoken-code capture
# ---------------------------------------------------------------------------

#: How a caller says a digit out loud.  "oh" is in here because people read
#: codes that way and an STT engine writes it down as a word.
_DIGIT_WORDS: dict[str, str] = {
    "zero": "0",
    "oh": "0",
    "o": "0",
    "nought": "0",
    "one": "1",
    "two": "2",
    "three": "3",
    "four": "4",
    "five": "5",
    "six": "6",
    "seven": "7",
    "eight": "8",
    "nine": "9",
}

#: Words that appear in the middle of a spoken code without breaking it up:
#: "it's 4 3 ... um ... 9 1".  Anything else ends the run, so "table for 4" in a
#: later sentence cannot be glued onto a code from an earlier one.
_CODE_FILLER: frozenset[str] = frozenset(
    {"um", "uh", "er", "erm", "and", "then", "sorry", "wait", "hang", "on"}
)

#: A verification code is between these lengths.  The platform sends six digits;
#: the range is loose enough to survive that being configured differently and
#: tight enough that a party size or a year is not mistaken for a code.
MIN_CODE_DIGITS: int = 4
MAX_CODE_DIGITS: int = 8

#: How many times the caller may fail to give us a usable code before a human
#: takes the call.  Unbounded re-asking is a loop with a friendly voice.
MAX_VERIFICATION_ATTEMPTS: int = 3


def extract_verification_code(text: str) -> str | None:
    """Pull a spoken verification code out of *text*, or return ``None``.

    Deterministic on purpose.  A code is the one thing in the dialog where a
    model paraphrasing helpfully is a failure: the digits either came back
    exactly or the caller has to say them again.

    Handles both "the code is 481502" and "four eight one five oh two", and
    stops the run at any word that is neither a digit nor filler so a number
    mentioned later in the same turn cannot be concatenated onto it.

    Args:
        text: The turn transcript.

    Returns:
        The digits as a string when a plausible code is present, else ``None``.
    """
    run: list[str] = []
    best: str = ""
    for token in re.findall(r"[a-z0-9]+", text.lower()):
        if token.isdigit():
            run.append(token)
            continue
        if token in _DIGIT_WORDS:
            run.append(_DIGIT_WORDS[token])
            continue
        if token in _CODE_FILLER and run:
            continue
        joined = "".join(run)
        if len(joined) > len(best):
            best = joined
        run = []
    joined = "".join(run)
    if len(joined) > len(best):
        best = joined
    if MIN_CODE_DIGITS <= len(best) <= MAX_CODE_DIGITS:
        return best
    return None


# ---------------------------------------------------------------------------
# What the caller hears
# ---------------------------------------------------------------------------

#: Said once the platform has accepted the code.  Short, because the read-back
#: follows it in the same turn.
VERIFIED_TEXT = "Lovely, thank you, that's you verified."

#: Said when the code the caller read out did not match.
WRONG_CODE_TEXT = (
    "Hmm, that code doesn't match what we've got, sorry. "
    "Could you read it out to me once more?"
)

#: Said when we could not hear a code in what the caller said.
NO_CODE_HEARD_TEXT = (
    "Sorry, I didn't catch that. Could you read me the code we've just sent you?"
)

#: Said when the code (or the grant) has aged out and a fresh one is going out.
EXPIRED_TEXT = (
    "That code has expired, sorry. I'll send you a fresh one now, "
    "and you can read that one out to me."
)


def _code_sent_text(channel: str) -> str:
    """Return the "I've sent you a code" line for *channel*.

    Args:
        channel: ``"sms"`` or ``"email"``.

    Returns:
        A line a person would actually say. The masked destination the platform
        returns is not read out: it is a placeholder string in most
        environments, and "I've sent it to your phone" is what the caller needs.
    """
    where = "your email" if channel == "email" else "your phone"
    return (
        "No problem, I just need to check it's really you first. "
        f"I've sent a code to {where} — could you read it out to me when it arrives?"
    )


def _idempotency_key(
    session_id: str | None, action: VerificationAction, round_: int
) -> str:
    """Build the stable send key for one verification round (Inv-9).

    Derived from session + action + round, so a replayed turn sends nothing and
    gets ``already_sent`` back with the SAME verification_id, while a genuinely
    new round (the previous code expired) gets a genuinely new code.
    """
    return f"verify:{session_id or 'unknown'}:{action.value}:{round_}"


async def _send_code(state: AgentState, action: VerificationAction) -> dict:
    """Ask the platform to send a code, and tell the caller it is coming.

    Args:
        state: Current ``AgentState``.
        action: The action the grant will authorize.

    Returns:
        A state update ending the turn with the verification_id recorded, or an
        escalation to human_handoff when the platform could not send a code
        (no verified destination, rate limited, unreachable) — there is no
        route to a grant from there, and Inv-11 forbids proceeding without one.
    """
    target = state.draft.request.target_booking_id if state.draft else None
    customer_id = state.customer.customer_id if state.customer else None
    req = VerificationRequest(
        target_booking_id=target,
        customer_id=customer_id,
        channel="sms",
        action=action,
        verification_idempotency_key=_idempotency_key(
            state.session_id, action, state.verification_attempts
        ),
    )
    sent = parse_verification_sent(await request_verification_call(req))
    if sent is None:
        _log.warning(
            "verify_identity: request_verification produced no verification for "
            "tenant=%s action=%s; escalating (Inv-11)",
            state.tenant_id,
            action,
        )
        return {
            "verification_outcome": "invalid",
            "terminal_state": "human_handoff",
        }

    _log.info(
        "verify_identity: code requested verification_id=%s action=%s",
        sent.verification_id,
        action,
    )
    return {
        "verification_id": sent.verification_id,
        "verification_outcome": "code_sent",
        "pending_verification_prompt": _code_sent_text(sent.channel),
    }


def _retry_or_handoff(state: AgentState, prompt: str) -> dict:
    """Re-ask for the code, or hand off once the attempt budget is spent."""
    attempts = state.verification_attempts + 1
    if attempts >= MAX_VERIFICATION_ATTEMPTS:
        _log.warning(
            "verify_identity: %d failed verification attempts; escalating (Inv-11)",
            attempts,
        )
        return {
            "verification_attempts": attempts,
            "verification_outcome": "invalid",
            "terminal_state": "human_handoff",
        }
    return {
        "verification_attempts": attempts,
        "verification_outcome": "code_retry",
        "pending_verification_prompt": prompt,
    }


async def verify_identity(state: AgentState) -> dict:
    """Request, collect and validate a platform-issued verification grant (§9.3, Inv-11).

    Determines the required VerificationAction from ``state.operation``.  With no
    verification in flight it asks the platform to send a code and ENDs the turn;
    otherwise it submits the code the caller just read out (falling back to a
    status poll when no digits were heard), validates scope + expiry, and returns
    the appropriate state update.

    Pass-through guard: when the operation does not require verification (create /
    inquire / unknown) this node routes back to capture_fields with no grant — this
    situation indicates an unexpected entry and is logged as a warning.

    This node performs NO mutation and NEVER sets/advances operation_id (Inv-11,
    02-J owns that).

    Returns:
        State update dict.  Possible keys:
          - verification_id / verification_grant_id (str | None)
          - verification_outcome (str) — pass_through | code_sent | code_retry |
            valid | expired | invalid
          - verification_attempts (int)
          - pending_verification_prompt (str) — what the caller hears
          - terminal_state (str) — only when escalating
    """
    required_action = _required_action(state.operation)

    # Pass-through: operation does not require a verification grant
    if required_action is None:
        _log.warning(
            "verify_identity entered for non-mutation operation=%s; "
            "routing back to capture_fields without grant (pass-through)",
            state.operation,
        )
        return {"verification_outcome": "pass_through"}

    if not state.verification_id:
        return await _send_code(state, required_action)

    # A code in the transcript is submitted (POST); with nothing to submit we
    # poll the existing challenge (GET), which is how a grant earned out of band
    # still counts.
    code = extract_verification_code(state.transcript_text or "")

    env = await check_verification_call(state.verification_id, code=code)
    grant = parse_verification_grant(env)

    outcome = validate_grant(
        grant,
        tenant_id=state.tenant_id,
        action=required_action,
        now=datetime.now(UTC),
    )

    if outcome is GrantOutcome.VALID:
        _log.info(
            "verify_identity: VALID grant=%s tenant=%s action=%s",
            grant.grant_id if grant else None,
            state.tenant_id,
            required_action,
        )
        return {
            "verification_grant_id": grant.grant_id,  # type: ignore[union-attr]
            "verification_outcome": "valid",
            "verification_attempts": 0,
            "pending_verification_prompt": VERIFIED_TEXT,
        }

    if outcome is GrantOutcome.EXPIRED or env.status == "expired":
        # Clearing verification_id is what makes the next turn send a fresh
        # code: a grant that has aged out cannot be revived, and re-checking the
        # same dead challenge for the rest of the call is the cycle this END
        # exists to stop.
        _log.info(
            "verify_identity: EXPIRED for tenant=%s action=%s; sending a fresh code",
            state.tenant_id,
            required_action,
        )
        return {
            "verification_id": None,
            "verification_grant_id": None,
            "verification_outcome": "expired",
            "verification_attempts": state.verification_attempts + 1,
            "pending_verification_prompt": EXPIRED_TEXT,
        }

    if grant is not None:
        # A grant came back and it is not ours: wrong tenant, wrong action, or
        # no grant_id at all.  That is not a caller misreading six digits, and
        # re-asking would invite whoever produced it to try again (Inv-11).
        _log.warning(
            "verify_identity: out-of-scope grant for tenant=%s action=%s; "
            "escalating to human_handoff (Inv-11)",
            state.tenant_id,
            required_action,
        )
        return {
            "verification_outcome": "invalid",
            "terminal_state": "human_handoff",
        }

    # No grant: either the caller read the code back wrong, or we heard no code
    # at all.  Bounded re-ask; never an advance (Inv-11).
    _log.info(
        "verify_identity: no grant yet (status=%s, code_heard=%s) for tenant=%s",
        env.status,
        code is not None,
        state.tenant_id,
    )
    return _retry_or_handoff(
        state, WRONG_CODE_TEXT if code is not None else NO_CODE_HEARD_TEXT
    )


def route_after_verify_identity(state: AgentState) -> str:
    """Route after verify_identity based on verification_outcome.

    Routes:
      - ``"human_handoff"`` when outcome is "invalid" (or terminal_state set).
      - ``END`` when a code has just been sent, when the caller has to try the
        code again, or when the grant EXPIRED — all three need something from
        the customer, which cannot arrive inside this turn.  Continuing to
        ``capture_fields`` sent the same mutation draft straight back here with
        the grant still cleared, which is a cycle, not a retry.
      - ``"capture_fields"`` for "valid" (advance) or "pass_through"
        (non-mutation operation — unexpected entry).
    """
    outcome = state.verification_outcome
    if outcome == "invalid" or state.terminal_state == "human_handoff":
        return "human_handoff"
    if outcome in {"expired", "code_sent", "code_retry"}:
        return END
    return "capture_fields"
