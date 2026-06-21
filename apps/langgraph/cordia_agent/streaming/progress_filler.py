"""§6.4 latency-band progress filler for in-flight tool calls."""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from cordia_agent.streaming.turn_manager import TurnManager
    from cordia_agent.streaming_models import ToolProgressFrame


class ProgressFiller:
    """Map ``tool.progress`` elapsed time to a neutral, announce-once filler phrase.

    Inv-16: phrases never imply success while a tool is still pending. The phrase is
    announced at most once per stable ``(turn_id, tool_name)`` via
    ``TurnManager.ack_tool_call``; the ack is consumed only when a phrase is actually
    emitted, so a sub-threshold blip cannot suppress a later announcement.
    """

    _BRIEF_MS = 300
    _CHECKING_MS = 1000
    _DEGRADED_MS = 5000

    def __init__(self, turn_manager: TurnManager) -> None:
        self._turn_manager = turn_manager

    def fill(self, frame: ToolProgressFrame) -> str | None:
        """Return a band-appropriate filler phrase, or ``None`` for no filler."""
        phrase = self._band_phrase(frame.elapsed_ms)
        if phrase is None:
            return None
        if not self._turn_manager.ack_tool_call(frame):
            return None
        return phrase

    def _band_phrase(self, elapsed_ms: int) -> str | None:
        if elapsed_ms < self._BRIEF_MS:
            return None
        if elapsed_ms < self._CHECKING_MS:
            return "just a moment"
        if elapsed_ms < self._DEGRADED_MS:
            return "checking availability"
        return "I'll follow up shortly"
