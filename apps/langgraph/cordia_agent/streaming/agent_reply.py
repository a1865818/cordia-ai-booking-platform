"""What the agent actually SAYS on a turn, derived from the nodes that ran.

The §8.3 graph has no "reply" channel.  Each node that needs to address the
customer writes its own state field — ``pending_consent_notice`` from the
consent gate, ``pending_clarify_prompt`` from clarify, ``read_back_text`` from
read-back, ``submission_message`` from submit — and the text driver simply
prints the whole state at the end.  That is fine for a script and useless for a
call: something has to decide, per turn, which of those fields the caller hears
and in what order.

This module is that decision, and it is deliberately a pure function of the
node updates rather than of the final state.  Two reasons:

- **Order.** A turn can legitimately produce more than one utterance — the
  read-back followed by the submission acknowledgement is the obvious case.
  Node-update order is the order the caller should hear them in; final state
  has lost that.
- **Novelty.** ``read_back_text`` stays set for the rest of the session once
  read-back has run.  Reading final state would replay it on every subsequent
  turn.  A node update only carries the field on the turn the node actually
  ran.

Both channels use this: the pipeline's ``AgentPort`` streams these segments as
``agent.text.delta`` frames so TTS can start on sentence one, and a text driver
can join them.
"""

from __future__ import annotations


#: Node name -> the state key that node writes customer-facing text into, in
#: the order nodes can run within a turn.  A node absent from this mapping is
#: silent by design (``capture_fields`` captures, ``check_candidates`` calls a
#: tool — neither addresses the customer).
REPLY_TEXT_KEYS: dict[str, str] = {
    "consent_gate": "pending_consent_notice",
    "answer_inquiry": "pending_inquiry_answer",
    "clarify": "pending_clarify_prompt",
    # verify_identity had no entry here, which is why the "I've sent you a code"
    # turn was silent: the node ENDs the turn, so if it says nothing the caller
    # gets the generic fallback and no idea an SMS is on its way.
    "verify_identity": "pending_verification_prompt",
    "read_back": "read_back_text",
    "submit_intent": "submission_message",
}

#: Said when ``human_handoff`` runs.  The node itself records no customer text —
#: it packages context for a human — but a caller cannot be left on a silent
#: line while that happens.
HANDOFF_TEXT = "Let me pass you over to one of the team who can help with that."

#: Said when a turn produced no customer-facing text at all.  On a text channel
#: an empty reply is merely odd; on a live call it is dead air, and the caller
#: will start talking over the next thing the agent says.  Callers that would
#: rather emit nothing pass ``fallback=None`` to :func:`reply_text`.
DEFAULT_FALLBACK = "Sorry, I didn't quite catch that. Could you say it again?"


def segment_for(node: str, update: object) -> str | None:
    """Return the customer-facing text *node* produced, if any.

    Args:
        node: The node name from a LangGraph ``updates`` stream chunk.
        update: That node's state-update payload.

    Returns:
        The text the caller should hear for this node, or ``None`` when the
        node said nothing on this turn.
    """
    if node == "human_handoff":
        return HANDOFF_TEXT

    key = REPLY_TEXT_KEYS.get(node)
    if key is None or not isinstance(update, dict):
        return None

    value = update.get(key)
    if not isinstance(value, str):
        return None

    text = value.strip()
    return text or None


def reply_text(
    segments: list[str], *, fallback: str | None = DEFAULT_FALLBACK
) -> str | None:
    """Join a turn's segments into the single utterance the caller hears.

    Args:
        segments: Segments in node order, as collected via :func:`segment_for`.
        fallback: Text to use when *segments* is empty, or ``None`` to return
            ``None`` and stay silent.

    Returns:
        The joined reply, the fallback, or ``None``.
    """
    joined = " ".join(segment for segment in segments if segment).strip()
    return joined or fallback
