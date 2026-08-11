"""Node: await_confirmation — §8.9 Confirmation Gate.

Waits for the customer's explicit yes/no response after the read-back.
Confirmed drafts proceed to submit_intent; rejected drafts loop back to
capture_fields so the customer can amend details.
"""

from __future__ import annotations

import re
import uuid
from datetime import UTC, datetime

from cordia_agent.state import AgentState  # noqa: TC001


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_AFFIRMATIVES: frozenset[str] = frozenset(
    {
        "yes",
        "yeah",
        "yep",
        "yup",
        "confirm",
        "confirmed",
        "that's right",
        "thats right",
        "correct",
        "go ahead",
        "proceed",
        "ok",
        "okay",
        "sure",
        "absolutely",
    }
)

# A negation anywhere in the utterance disqualifies it as a clean confirmation
# (e.g. "not correct", "don't go ahead", "no", "cancel that", "that's wrong").
# Conservative by design: a mixed/negated utterance is never treated as an
# explicit confirmation (Inv-1 — only unambiguous confirmation advances).
_NEGATIONS: frozenset[str] = frozenset(
    {
        "no",
        "nope",
        "not",
        "don't",
        "dont",
        "never",
        "cancel",
        "stop",
        "wait",
        "wrong",
        "incorrect",
        "n't",
    }
)

_WORD_RE = re.compile(r"[a-z']+")


def is_explicit_confirmation(text: str) -> bool:
    """Return True iff ``text`` is an unambiguous affirmation (Inv-1).

    Deterministic — no LLM. Rejects any utterance containing a negation token
    (so "not correct" / "don't go ahead" / "no" never confirm) and matches
    affirmatives on WORD boundaries (so "incorrect" never matches "correct").
    """
    lowered = text.lower().strip()
    if not lowered:
        return False

    tokens = set(_WORD_RE.findall(lowered))
    # Reject negated/ambiguous utterances outright.
    if tokens & _NEGATIONS or any("n't" in tok for tok in tokens):
        return False

    # Word-boundary match for each affirmative phrase (avoids substring false
    # positives like "incorrect" → "correct").
    return any(
        re.search(rf"\b{re.escape(affirmative)}\b", lowered)
        for affirmative in _AFFIRMATIVES
    )


def consent_satisfied(disclosures_presented: list[str]) -> bool:
    """Return True iff the required consent disclosures have been presented (§5.5 / §14).

    STUB: the real required-disclosure set is owned by ticket 14.  For now,
    non-empty ``disclosures_presented`` is the gate condition.
    """
    return len(disclosures_presented) > 0


# ---------------------------------------------------------------------------
# Node
# ---------------------------------------------------------------------------


async def await_confirmation(state: AgentState) -> dict:
    """Gate on explicit customer confirmation before submitting (§8.9 / Inv-1).

    Records an immutable ``Confirmation`` ONLY when ALL of the following hold:
    1. ``state.transcript_is_final`` is True (Inv-1: partial frames cannot confirm).
    2. ``not state.read_back_interrupted`` (§8.9: interrupted read-back is never confirmed).
    3. ``state.read_back_version`` is set (a read-back has occurred).
    4. ``is_explicit_confirmation(state.transcript_text)`` is True.
    5. ``consent_satisfied(state.disclosures_presented)`` is True (§5.5 / §14).

    If consent is NOT satisfied → signal to route back to read_back via
    capture_fields (do NOT record a Confirmation).

    Returns a partial state update dict.
    """
    # §8.9: interrupted read-back → never confirmed
    if state.read_back_interrupted:
        return {}

    # Inv-1: partial transcript cannot confirm
    if not state.transcript_is_final:
        return {}

    # Must have a read-back to confirm against
    if not state.read_back_version:
        return {}

    # Must have an explicit confirmation utterance
    if not is_explicit_confirmation(state.transcript_text or ""):
        return {}

    # §5.5 / §14 consent gate: refuse to record without disclosures
    if not consent_satisfied(state.disclosures_presented):
        # Signal: consent not satisfied — route back (caller reads this via router)
        return {"confirmation": None}

    # All conditions met — build and record the Confirmation (§8.8)
    from cordia_agent.models.intent import Confirmation, IntentRevision

    if state.current_revision is None:
        # No revision yet — cannot record a confirmation without a bound revision
        return {}

    current_rev = IntentRevision.model_validate(state.current_revision)

    # §8.8: intent_payload_hash MUST equal the current revision's payload_hash
    # so that confirmation_binds_revision is True.
    confirmation = Confirmation(
        confirmation_id=str(uuid.uuid4()),
        intent_id=current_rev.intent_id,
        intent_revision=current_rev.revision,
        intent_payload_hash=current_rev.payload_hash,  # §8.8 binding
        identity=state.verification_grant_id or "anonymous_session",
        confirmation_channel=_derive_channel(state),
        confirmed_at=datetime.now(tz=UTC),
        source_turn_id=state.current_turn_id or "unknown",
        read_back_version=state.read_back_version,
        disclosures_presented=list(state.disclosures_presented),
        verification_level=_normalise_verification_level(state.verification_level),
    )

    return {
        "confirmation": confirmation.model_dump(mode="json"),
        "last_confirmed_revision": current_rev.revision,
    }


#: The ratified `verification_level` vocabulary (spec 04 §8.8 / spec 08). The API rejects
#: anything else outright — it used to coerce it to `unverified`, which meant a customer the
#: agent had genuinely identity-verified was recorded as unverified and nothing said so (R15).
VERIFICATION_LEVELS = frozenset(
    {"unverified", "channel_verified", "identity_verified", "high_assurance"}
)


def _normalise_verification_level(raw: str | None) -> str:
    """Coerce a captured assurance level to a ratified vocabulary member.

    Absent, or the historical ``"none"`` sentinel this node used to emit, both mean "we did not
    verify anyone" — which the vocabulary spells ``unverified``. Anything else unrecognised is
    treated the same way rather than forwarded: the API would reject it with a 400, and losing a
    whole confirmed submission to a vocabulary typo is worse than recording the honest floor.
    """
    if raw is None:
        return "unverified"
    candidate = raw.strip().lower()
    return candidate if candidate in VERIFICATION_LEVELS else "unverified"


def _derive_channel(state: AgentState) -> str:
    """Derive the confirmation channel from state (fallback to 'voice')."""
    if state.draft and state.draft.source:
        return state.draft.source.channel.value
    return "voice"


# ---------------------------------------------------------------------------
# Router
# ---------------------------------------------------------------------------


def route_after_await_confirmation(state: AgentState) -> str:
    """Route after await_confirmation.

    - ``"submit_intent"`` when a valid Confirmation is recorded.
    - ``"capture_fields"`` when:
        * consent not satisfied (gate refused to record, route back for re-read-back)
        * no explicit confirmation / not final / interrupted (customer changed
          details or said no — loop back to capture/clarify per §8.3 edges)
    """
    if state.confirmation is not None:
        return "submit_intent"
    return "capture_fields"
