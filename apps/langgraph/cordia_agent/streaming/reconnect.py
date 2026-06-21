"""Session re-binding handler for streaming voice-pipeline reconnects."""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from typing import TYPE_CHECKING, Protocol


if TYPE_CHECKING:
    from cordia_agent.contracts.operation_status import NormalizedOperationStatus
    from cordia_agent.streaming.turn_manager import TurnManager


class ReconnectOutcome(str, Enum):
    """Result of a reconnect attempt."""

    RESUMED = "resumed"
    FAILED = "failed"
    HANDOFF_SIGNALLED = "handoff_signalled"


@dataclass(frozen=True, slots=True)
class ReconnectSessionContext:
    """Identity fields for session rebinding — propagated, not resolved (Inv-13)."""

    session_id: str
    correlation_id: str
    tenant_id: str
    turn_id: str


class SessionLookup(Protocol):
    """Port: look up an existing session by channel reference."""

    async def lookup(self, channel_ref: str) -> ReconnectSessionContext | None: ...


class AgentResumeHook(Protocol):
    """Port: signal the agent to resume from persisted draft (§8.9).

    Returns the recovered operation status from ``get_operation_status``, or
    ``None`` when no in-flight operation exists.
    """

    async def resume(
        self, context: ReconnectSessionContext
    ) -> NormalizedOperationStatus | None: ...


class HandoffHook(Protocol):
    """Port: signal human handoff on repeated reconnect failure (§8.12)."""

    async def signal(self, context: ReconnectSessionContext | None) -> None: ...


class ReconnectHandler:
    """Session re-binding handler for the streaming pipeline.

    Re-establishes a streaming session after media drop / channel re-attach
    under the SAME session_id/correlation_id. Never finalizes in-flight turns
    (Inv-1). Never mints operation_id (Inv-2). Signals handoff after
    max_reconnect_attempts consecutive failures (§8.12).
    """

    def __init__(
        self,
        *,
        session_lookup: SessionLookup,
        agent_resume_hook: AgentResumeHook,
        handoff_hook: HandoffHook,
        turn_manager: TurnManager,
        max_reconnect_attempts: int = 3,
    ) -> None:
        self._session_lookup = session_lookup
        self._agent_resume_hook = agent_resume_hook
        self._handoff_hook = handoff_hook
        self._turn_manager = turn_manager
        self._max_reconnect_attempts = max_reconnect_attempts
        self._consecutive_failures = 0
        self._session_context: ReconnectSessionContext | None = None
        self._last_known_context: ReconnectSessionContext | None = None

    @property
    def session_context(self) -> ReconnectSessionContext | None:
        return self._session_context

    @property
    def consecutive_failures(self) -> int:
        return self._consecutive_failures

    async def attempt_reconnect(self, channel_ref: str) -> ReconnectOutcome:
        context = await self._session_lookup.lookup(channel_ref)

        if context is None:
            self._consecutive_failures += 1
            if self._consecutive_failures >= self._max_reconnect_attempts:
                await self._handoff_hook.signal(self._last_known_context)
                return ReconnectOutcome.HANDOFF_SIGNALLED
            return ReconnectOutcome.FAILED

        self._session_context = context
        self._last_known_context = context
        self._consecutive_failures = 0

        await self._agent_resume_hook.resume(context)

        return ReconnectOutcome.RESUMED
