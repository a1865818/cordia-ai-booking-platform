"""Consent gate node — spec 14 §5.1/§5.2 (design §24.1), wired per spec 02.

Runs immediately after ``resolve_tenant`` and before any transcript-bearing
node. Resolves the jurisdiction policy for (tenant, channel) via the API,
plays/displays the required notice, captures the customer's outcome from the
turn transcript, and persists the ``SessionConsentState`` through the Booking
API (single-writer rule, D12 — the gate updates ``consent_*``; the channel
gateway creates the session row).

The policy is resolved ONCE per session and stashed on state
(``consent_policy``), so the reply turn records against the exact policy
version the customer was shown — no second round trip, no version skew.

Fail-closed defaults (spec 14 §6):
- Policy resolution fails      → continue NON-recorded this turn; the gate
  retries on the next turn (``consent_state`` stays ``None``).
- Customer declines            → ``alternative_offered`` outcome, continue
  NON-recorded (structured intent still captured; no transcript persisted).
- record_consent fails         → continue NON-recorded; the classified reply
  is stashed (``consent_reply``) and the persist retried next turn WITHOUT
  re-asking the customer.
- Notice required, no reply    → emit the notice and END the turn (await).

The gate never evaluates jurisdiction policy locally and never lets transcript
text change authorization — it only maps an explicit yes/no reply onto the
ratified consent outcomes.
"""

from __future__ import annotations

import datetime
import re
from typing import Literal

from langgraph.graph import END

from cordia_agent.contracts.security import SessionConsentState
from cordia_agent.security.policy import set_redaction_policy
from cordia_agent.state import AgentState, Channel
from cordia_agent.tools.client_binding import (
    parse_consent_policy,
    record_consent_call,
    resolve_consent_policy_call,
)
from cordia_agent.tools.contracts import ConsentPolicyView, RecordConsentInput


DEFAULT_NOTICE = (
    "This conversation may be recorded and transcribed to process your "
    "booking. Is that okay, or would you prefer to continue without recording?"
)

# Channel enum (voice/sms/web/email) → spec-14 notice_channel literal.
_NOTICE_CHANNEL: dict[Channel, str] = {
    Channel.voice: "voice",
    Channel.sms: "messaging",
    Channel.web: "web",
    Channel.email: "email",
}

_GRANT_PATTERN = re.compile(
    r"(?i)\b(yes|yeah|yep|sure|okay|ok|fine|that's fine|go ahead|of course|no problem)\b"
)
_DECLINE_PATTERN = re.compile(
    r"(?i)(?:\b(?:nope|don't|do not|without recording|rather not|decline|stop recording)\b"
    r"|\bno\b(?!\s+problem))"
)

# Uncertainty beats both: "I'm not sure" must never read as consent (§6
# fail-closed) — the gate keeps awaiting instead.
_UNCERTAIN_PATTERN = re.compile(r"(?i)\b(not sure|unsure|maybe|hard to say)\b")


def classify_consent_response(text: str) -> Literal["granted", "declined"] | None:
    """Map an explicit customer reply onto a consent outcome, else ``None``.

    Precedence (fail-closed): uncertainty → ``None`` (re-ask); decline wins
    over grant when both match ("no, don't record, but okay let's continue"
    must not be read as consent); a bare affirmative grants.
    """
    if _UNCERTAIN_PATTERN.search(text):
        return None
    if _DECLINE_PATTERN.search(text):
        return "declined"
    if _GRANT_PATTERN.search(text):
        return "granted"
    return None


def _notice_channel(state: AgentState) -> str:
    channel = state.source.channel if state.source is not None else Channel.voice
    return _NOTICE_CHANNEL.get(channel, "voice")


def _channel_value(state: AgentState) -> str:
    channel = state.source.channel if state.source is not None else Channel.voice
    return channel.value


async def _record(
    state: AgentState,
    policy: ConsentPolicyView,
    *,
    reply: Literal["granted", "declined"] | None,
    outcome: Literal["granted", "declined", "not_required", "alternative_offered"],
    recording_allowed: bool,
) -> dict:
    """Persist the outcome via record_consent; fail-closed on any failure.

    On persist failure the classified *reply* (when there is one) is stashed
    in ``consent_reply`` so the next turn retries the persist directly instead
    of re-emitting the notice and discarding the customer's answer.
    """
    consent = SessionConsentState(
        consent_policy_version=policy.consent_policy_version,
        jurisdiction_policy_id=policy.jurisdiction_policy_id,
        outcome=outcome,
        recording_allowed=recording_allowed,
        captured_at=datetime.datetime.now(datetime.UTC),
        notice_channel=_notice_channel(state),  # type: ignore[arg-type]
    )
    env = await record_consent_call(
        RecordConsentInput(session_id=state.session_id or "", consent=consent)
    )
    if not env.ok:
        # Fail-closed: not persisted ⇒ non-recorded this turn; retried next
        # turn via consent_reply (consent_state stays None, §8 failure table).
        return {
            "recording_allowed": False,
            "pending_consent_notice": None,
            "consent_reply": reply,
        }
    return {
        "consent_state": consent.model_dump(mode="json"),
        "recording_allowed": recording_allowed,
        "pending_consent_notice": None,
        "consent_reply": None,
    }


async def _record_reply(
    state: AgentState,
    policy: ConsentPolicyView,
    reply: Literal["granted", "declined"],
) -> dict:
    """Map a classified reply onto its ratified outcome and persist it."""
    if reply == "granted":
        return await _record(
            state, policy, reply=reply, outcome="granted", recording_allowed=True
        )
    # Declined ⇒ non-recorded alternative (§5.2 AlternativeOffered).
    return await _record(
        state,
        policy,
        reply=reply,
        outcome="alternative_offered",
        recording_allowed=False,
    )


def _install_redaction(policy: ConsentPolicyView | None) -> ConsentPolicyView | None:
    """Install the tenant redaction policy the moment a policy view is in hand.

    Spec 14 §4.1: the API resolves the tenant's ``RedactionPolicy`` and ships
    it on the ConsentPolicyView; installing it here (first resolution AND
    stashed-policy reuse — every path that yields a policy) makes the tenant's
    configured sensitive-field selectors live for the rest of the session.
    Absent ⇒ the always-on baseline stays active. Returns *policy* unchanged
    so call sites can wrap in place.
    """
    if policy is not None and policy.redaction is not None:
        set_redaction_policy(policy.redaction)
    return policy


def _stashed_policy(state: AgentState) -> ConsentPolicyView | None:
    if state.consent_policy is None:
        return None
    try:
        return _install_redaction(
            ConsentPolicyView.model_validate(state.consent_policy)
        )
    except Exception:
        return None


async def _resolve_policy(state: AgentState) -> ConsentPolicyView | None:
    env = await resolve_consent_policy_call(_channel_value(state))
    return _install_redaction(parse_consent_policy(env))


async def consent_gate(state: AgentState) -> dict:
    """Resolve policy, present the notice, capture + persist the outcome (§5.1)."""
    # Already recorded for this session — idempotent pass-through.
    if state.consent_state is not None:
        return {}

    # A prior turn classified the reply but the persist failed — retry it.
    # The notice is NOT pending on this turn, so the current transcript is a
    # normal conversational utterance, not a consent answer: it may only
    # override the stash in the FAIL-SAFE direction. A fresh explicit decline
    # ("actually, stop recording") downgrades a stashed grant; a stray
    # affirmative ("yes please", answering an unrelated question) must NEVER
    # upgrade a stashed decline (consent fail-open).
    if state.consent_reply in ("granted", "declined"):
        reply: Literal["granted", "declined"] = state.consent_reply  # type: ignore[assignment]
        if classify_consent_response(state.transcript_text or "") == "declined":
            reply = "declined"
        policy = _stashed_policy(state) or await _resolve_policy(state)
        if policy is None:
            return {"recording_allowed": False, "consent_reply": reply}
        return await _record_reply(state, policy, reply)

    # Awaiting the customer's reply to a previously-emitted notice.
    if state.pending_consent_notice is not None:
        response = classify_consent_response(state.transcript_text or "")
        if response is None:
            # Unparseable reply — keep awaiting; stay non-recorded meanwhile.
            return {"recording_allowed": False}
        policy = _stashed_policy(state) or await _resolve_policy(state)
        if policy is None:
            # Policy stash lost AND re-resolution failed — stash the reply so
            # the next turn retries without re-asking, and CLEAR the pending
            # notice (the customer already answered; leaving it set would
            # route to END and re-await/re-play the notice).
            return {
                "recording_allowed": False,
                "consent_reply": response,
                "pending_consent_notice": None,
            }
        return await _record_reply(state, policy, response)

    # First pass for this session — resolve the jurisdiction policy ONCE.
    policy = await _resolve_policy(state)
    if policy is None:
        # Fail-closed: no policy ⇒ non-recorded; retry next turn.
        return {"recording_allowed": False}

    if not policy.notice_required:
        return await _record(
            state, policy, reply=None, outcome="not_required", recording_allowed=True
        )

    # Notice required — emit it, stash the policy, await the reply (END).
    return {
        "pending_consent_notice": policy.notice_text or DEFAULT_NOTICE,
        "consent_policy": policy.model_dump(mode="json"),
        "recording_allowed": False,
    }


def route_after_consent_gate(state: AgentState) -> str:
    """Route to load_context once consent is settled; END while awaiting a reply.

    ``consent_state`` set ⇒ settled (recorded or non-recorded) — continue.
    ``pending_consent_notice`` set (and not settled) ⇒ the notice was just
    emitted or the reply was unparseable — END the turn and await the customer.
    Neither ⇒ policy resolution or persist failed — continue NON-recorded
    (fail-closed; retried next turn via first-pass or ``consent_reply``).
    """
    if state.consent_state is not None:
        return "load_context"
    if state.pending_consent_notice is not None:
        return END
    return "load_context"
