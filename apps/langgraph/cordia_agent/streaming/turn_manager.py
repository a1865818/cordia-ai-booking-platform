"""Pure turn-management helpers for streaming voice-pipeline frames."""

from __future__ import annotations

from dataclasses import dataclass, field

from cordia_agent.streaming_models import (
    AgentTextDeltaFrame,
    AgentTextFinalFrame,
    AudioStartedFrame,
    BargeInFrame,
    ToolProgressFrame,
    TranscriptFinalFrame,
    TranscriptPartialFrame,
    TtsAudioDeltaFrame,
    TurnCancelledFrame,
)


type StreamFrame = (
    AudioStartedFrame
    | TranscriptPartialFrame
    | TranscriptFinalFrame
    | AgentTextDeltaFrame
    | AgentTextFinalFrame
    | ToolProgressFrame
    | TtsAudioDeltaFrame
    | BargeInFrame
    | TurnCancelledFrame
)


def is_committable(frame: StreamFrame) -> bool:
    """Return whether a frame can be committed into conversation state."""
    return isinstance(frame, TranscriptFinalFrame | AgentTextFinalFrame)


@dataclass
class TurnManager:
    """In-memory turn state for one streaming pipeline process."""

    _next_seq_by_session_id: dict[str, int] = field(default_factory=dict)
    _seen_frame_keys: set[tuple[str, int]] = field(default_factory=set)
    _non_authoritative_turn_ids: set[str] = field(default_factory=set)
    _acknowledged_tool_calls: set[tuple[str, str]] = field(default_factory=set)

    def next_seq(self, session_id: str) -> int:
        """Return the next monotonic sequence number for a session."""
        seq = self._next_seq_by_session_id.get(session_id, 0)
        self._next_seq_by_session_id[session_id] = seq + 1
        return seq

    def is_already_seen(self, frame: StreamFrame) -> bool:
        """Return whether this frame's `(session_id, seq)` was already recorded."""
        return self._frame_key(frame) in self._seen_frame_keys

    def record_seen(self, frame: StreamFrame) -> bool:
        """Record a frame key and return whether it had already been seen."""
        key = self._frame_key(frame)
        already_seen = key in self._seen_frame_keys
        self._seen_frame_keys.add(key)
        return already_seen

    def handle_cancellation(self, frame: TurnCancelledFrame) -> None:
        """Apply cancellation authority rules for a cancelled turn."""
        if not frame.material_content_delivered:
            self._non_authoritative_turn_ids.add(frame.cancelled_turn_id)

    def is_authoritative_turn(self, turn_id: str) -> bool:
        """Return whether a turn is still authoritative."""
        return turn_id not in self._non_authoritative_turn_ids

    def ack_tool_call(self, frame: ToolProgressFrame) -> bool:
        """Acknowledge a stable `(turn_id, tool_name)` once.

        Returns True for the first acknowledgement and False for repeats.
        """
        key = (frame.turn_id, frame.tool_name)
        is_first_ack = key not in self._acknowledged_tool_calls
        self._acknowledged_tool_calls.add(key)
        return is_first_ack

    @staticmethod
    def _frame_key(frame: StreamFrame) -> tuple[str, int]:
        return frame.session_id, frame.seq
