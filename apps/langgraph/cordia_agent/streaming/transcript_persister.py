"""Transcript-turn persistence port for the streaming pipeline (spec 14 §4.3/§5.2).

The pipeline is a pure in-memory frame pipe; persisting a committed, ALREADY-SCRUBBED
``transcript.final`` frame is an optional side port so the pipe's behavior is byte-identical
when no persister is wired. Two fail postures, deliberately different:

- **Fail-closed for persistence** (Inv-12/§5.2): when consent is unknown or not granted the
  persister makes NO call at all — the server re-enforces the gate authoritatively either way.
- **Fail-open for the pipe** (§8 failure table): a persist error must never break the caller's
  turn — the pipeline swallows and logs; a lost turn object is recoverable, a dropped call is not.
"""

from __future__ import annotations

import logging
from typing import TYPE_CHECKING, Protocol

from cordia_agent.tools.client_binding import append_transcript_turn_call
from cordia_agent.tools.contracts import TranscriptTurnInput


if TYPE_CHECKING:
    from collections.abc import Callable

    from cordia_agent.streaming_models import TranscriptFinalFrame


_log = logging.getLogger(__name__)


class TranscriptPersister(Protocol):
    """Port: persist one committed, scrubbed final transcript frame (best-effort)."""

    async def persist(self, frame: TranscriptFinalFrame) -> None: ...


class ToolTranscriptPersister:
    """Persists redacted final turns through the Booking API tool client (spec 14 §4.3).

    ``recording_allowed`` is supplied by the hosting layer from agent state (the consent
    gate's outcome). ``None``/``False`` ⇒ no call (fail-closed); the API re-checks the
    session row authoritatively regardless, so a stale ``True`` here can never persist a
    non-consented turn.
    """

    def __init__(self, *, recording_allowed: Callable[[], bool | None]) -> None:
        self._recording_allowed = recording_allowed

    async def persist(self, frame: TranscriptFinalFrame) -> None:
        if self._recording_allowed() is not True:
            return
        # frame.text is already scrubbed by the pipeline's pre-emit redaction; the
        # contract documents (and the API re-enforces) that no raw text rides here.
        await append_transcript_turn_call(
            TranscriptTurnInput(
                session_id=frame.session_id,
                seq=frame.seq,
                turn_id=frame.turn_id,
                speaker="customer",
                text=frame.text,
            )
        )
