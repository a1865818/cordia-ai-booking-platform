"""Agent-port seam between the streaming pipeline (spec 01) and the agent (spec 02).

Spec 02 implements ``AgentPort``; spec 01 ships only this Protocol + a test fake,
keeping the streaming layer buildable in isolation.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Protocol

from cordia_agent.streaming_models import (
    AgentTextDeltaFrame,
    AgentTextFinalFrame,
    ToolProgressFrame,
)


if TYPE_CHECKING:
    from collections.abc import AsyncIterator

    from cordia_agent.streaming_models import TranscriptFinalFrame


type AgentOutputFrame = AgentTextDeltaFrame | AgentTextFinalFrame | ToolProgressFrame


class AgentPort(Protocol):
    """Consume a committed final transcript and stream agent output frames.

    Implementations own their own envelope (tenant/session/turn/correlation/seq);
    the pipeline passes their frames through unchanged.
    """

    def process(self, frame: TranscriptFinalFrame) -> AsyncIterator[AgentOutputFrame]:
        """Yield agent text deltas/finals and tool-progress frames for a turn."""
