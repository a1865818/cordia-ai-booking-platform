"""``AgentPort`` implementation backed by the §8.3 agent graph (handoff §6.3).

Spec 01 shipped ``AgentPort`` as a Protocol with a test fake and no production
implementation, which is the second of the two holes in the voice channel: the
streaming pipeline could recognise speech and could synthesize speech, but
nothing joined a committed transcript to the graph that books.

This is that join, and it is the whole of it.  The field mapping was already
settled by ``AgentInputState`` — ``frame.text`` is ``transcript_text``,
``frame.turn_id`` is ``current_turn_id``, and only finals reach the port so
``transcript_is_final`` is always ``True``.  What is left is turn framing,
which is where the real work is.

One frame in, one graph turn out
    A ``TranscriptFinalFrame`` is one customer utterance, and the graph now
    terminates a turn on its own (see ``graph.py``: clarify, read-back-awaiting
    confirmation and the consent notice all END).  So ``process`` runs the
    graph to completion and streams what it says on the way.  The session's
    checkpointer carries state between turns; a port constructed against a
    graph compiled WITHOUT a checkpointer would forget the booking between
    utterances, so :class:`GraphAgentPort` refuses one at construction.

Progress, honestly
    ``submit_intent`` and ``check_candidates`` make real network calls, and a
    caller hears the resulting gap as dead air.  LangGraph's ``tasks`` stream
    mode reports node entry, so the port knows when a slow node is in flight
    and emits ``ToolProgressFrame``s on a timer with the ELAPSED time actually
    measured — not a fabricated constant.  ``ProgressFiller`` downstream turns
    those into "just a moment" / "checking availability" at the §6.4 latency
    bands.  Nothing is emitted for a node that returns promptly.

What it does not do
    It does not interpret, summarise, or generate — every word it emits was
    written by a node.  See ``agent_reply`` for the mapping.
"""

from __future__ import annotations

import asyncio
import logging
import time
from contextlib import aclosing
from dataclasses import dataclass
from datetime import UTC, datetime
from typing import TYPE_CHECKING, Any

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming.agent_reply import (
    DEFAULT_FALLBACK,
    reply_text,
    segment_for,
)
from cordia_agent.streaming_models import (
    AgentTextDeltaFrame,
    AgentTextFinalFrame,
    ToolProgressFrame,
)


if TYPE_CHECKING:
    from collections.abc import AsyncIterator

    from cordia_agent.streaming.agent_port import AgentOutputFrame
    from cordia_agent.streaming_models import TranscriptFinalFrame


logger = logging.getLogger(__name__)

#: Nodes whose latency a caller can hear, mapped to the ``tool_name`` reported
#: on their progress frames.  Everything else in the graph is local work that
#: completes inside a few milliseconds and needs no filler.
SLOW_NODES: dict[str, str] = {
    "resolve_tenant": "resolve_tenant",
    "consent_gate": "resolve_consent_policy",
    "load_context": "find_customer_context",
    "capture_fields": "extract_slots",
    "check_candidates": "check_candidate_availability",
    "verify_identity": "request_verification",
    "submit_intent": "submit_confirmed_intent",
    "human_handoff": "request_human_handoff",
}

#: How often an in-flight slow node produces a progress frame.  Below the
#: ``ProgressFiller``'s 300 ms floor so the first band is reachable, and coarse
#: enough that a fast node never emits one at all.
DEFAULT_PROGRESS_INTERVAL_SECONDS = 0.25

#: Ceiling on progress frames per node, so a genuinely stuck call escalates to
#: the filler's degraded band and then stops rather than emitting forever.
MAX_PROGRESS_FRAMES_PER_NODE = 40


@dataclass(frozen=True, slots=True)
class AgentSessionContext:
    """Per-call identity the port stamps onto every graph input and frame.

    Everything here is established at channel ingress — the signed
    ``TenantContext`` is minted by the ingress webhook and the ids come with
    it — and is constant for the life of the call.

    Attributes:
        signed_tenant_context: Compact-JWS ``TenantContext`` from ingress.  The
            graph verifies it on every turn (Inv-13); the port only carries it.
        session_id: Dialog session id.  Also the checkpointer thread id, which
            is what makes a multi-turn call one conversation.
        tenant_id: Opaque tenant scope stamped on outgoing frames.  Propagated,
            never resolved here (Inv-13).
        correlation_id: Correlation id begun at ingress (§25.1).
        channel: Origin channel for ``SourceInfo``.
        language: BCP-47 language tag for ``SourceInfo``.
        recursion_limit: LangGraph recursion budget per turn.
    """

    signed_tenant_context: str
    session_id: str
    tenant_id: str
    correlation_id: str
    channel: str = "voice"
    language: str = "en-AU"
    recursion_limit: int = 50


class GraphAgentPort:
    """Drive one agent-graph turn per committed transcript and stream the reply."""

    def __init__(
        self,
        graph: Any,
        *,
        session: AgentSessionContext,
        fallback_text: str | None = DEFAULT_FALLBACK,
        progress_interval_seconds: float = DEFAULT_PROGRESS_INTERVAL_SECONDS,
    ) -> None:
        """Bind a compiled graph to one call.

        Args:
            graph: A compiled agent graph, built WITH a checkpointer.
            session: Per-call identity; see :class:`AgentSessionContext`.
            fallback_text: Said when a turn produced no customer-facing text.
                ``None`` stays silent instead (see ``agent_reply``).
            progress_interval_seconds: Cadence for in-flight progress frames.

        Raises:
            ValueError: if *graph* was compiled without a checkpointer, which
                would silently discard the booking between turns, or if the
                progress interval is not positive.
        """
        if getattr(graph, "checkpointer", None) is None:
            raise ValueError(
                "GraphAgentPort requires a graph compiled WITH a checkpointer: a "
                "call spans several turns and the draft lives in the checkpoint. "
                "Pass build_agent_graph(checkpointer=...)."
            )
        if progress_interval_seconds <= 0:
            raise ValueError(
                "progress_interval_seconds must be > 0 "
                f"(got {progress_interval_seconds!r})"
            )

        self._graph = graph
        self._session = session
        self._fallback_text = fallback_text
        self._progress_interval = progress_interval_seconds

    @property
    def config(self) -> dict[str, Any]:
        """The LangGraph config this port drives the graph with."""
        return {
            "configurable": {"thread_id": self._session.session_id},
            "recursion_limit": self._session.recursion_limit,
        }

    async def state(self) -> Any:
        """Return the current checkpointed state snapshot for this call."""
        return await self._graph.aget_state(self.config)

    async def process(
        self, frame: TranscriptFinalFrame
    ) -> AsyncIterator[AgentOutputFrame]:
        """Run one graph turn for a committed transcript and stream its output.

        Args:
            frame: The committed final transcript.  Inv-1 guarantees only
                finals reach here, so the graph is always fed
                ``transcript_is_final=True``.

        Yields:
            ``ToolProgressFrame`` while a slow node is in flight, then one
            ``AgentTextDeltaFrame`` per reply segment, then a single
            ``AgentTextFinalFrame`` carrying the whole utterance.

        Raises:
            Exception: whatever the graph raises.  A failed turn is NOT turned
                into a cheerful reply — ``InvalidTenantContext`` in particular
                must reach the caller of the pipeline, not the caller on the
                phone.
        """
        segments: list[str] = []
        # aclosing, not a bare `async for`: a barge-in abandons this generator
        # mid-turn, and without it the inner stream (and the task pumping the
        # graph into it) would be finalised whenever the event loop's
        # async-generator hook got round to it rather than right now.
        async with aclosing(self._stream_turn(frame)) as events:
            async for event in events:
                if isinstance(event, ToolProgressFrame):
                    yield event
                    continue

                node, update = event
                segment = segment_for(node, update)
                if segment is None:
                    continue
                # The separator rides on the delta, not the join, so the deltas
                # concatenate to EXACTLY the final text.  ``TTSChunker`` diffs
                # the final against the deltas it has already spoken; any
                # mismatch and the caller hears the tail of the reply twice.
                yield self._delta(frame, segment if not segments else f" {segment}")
                segments.append(segment)

        text = reply_text(segments, fallback=self._fallback_text)
        if text is not None:
            yield self._final(frame, text)

    async def _stream_turn(
        self, frame: TranscriptFinalFrame
    ) -> AsyncIterator[ToolProgressFrame | tuple[str, object]]:
        """Stream one graph turn, interleaving progress ticks with node updates.

        The graph stream is drained by a background task into a queue so this
        coroutine can wake on a timer as well as on graph output.  Without that
        it would be parked inside ``astream`` for the whole of a slow node and
        could not emit anything while the caller waits.
        """
        queue: asyncio.Queue[tuple[str, Any] | None] = asyncio.Queue()
        failure: list[BaseException] = []

        async def pump() -> None:
            try:
                async for mode, chunk in self._graph.astream(
                    self._graph_input(frame),
                    config=self.config,
                    stream_mode=["updates", "tasks"],
                ):
                    await queue.put((mode, chunk))
            except Exception as exc:
                # Captured, not swallowed: re-raised below, after the consumer
                # has been let out of the queue loop.  Deliberately NOT
                # BaseException — a CancelledError here is our own cancellation
                # of this task and must propagate rather than be replayed at
                # the caller as if the graph had failed.
                failure.append(exc)
            finally:
                await queue.put(None)

        task = asyncio.create_task(pump(), name="cordia-agent-graph-turn")
        in_flight: str | None = None
        started_at = 0.0
        ticks = 0

        try:
            while True:
                try:
                    event = await asyncio.wait_for(
                        queue.get(), timeout=self._progress_interval
                    )
                except TimeoutError:
                    if in_flight is not None and ticks < MAX_PROGRESS_FRAMES_PER_NODE:
                        ticks += 1
                        yield self._progress(frame, in_flight, started_at)
                    continue

                if event is None:
                    break

                mode, chunk = event
                if mode == "tasks":
                    node = chunk.get("name") if isinstance(chunk, dict) else None
                    if not isinstance(node, str):
                        continue
                    if isinstance(chunk, dict) and "result" not in chunk:
                        in_flight = node if node in SLOW_NODES else None
                        started_at = time.monotonic()
                        ticks = 0
                    else:
                        in_flight = None
                    continue

                if mode == "updates" and isinstance(chunk, dict):
                    for node, update in chunk.items():
                        yield (node, update)
        finally:
            if not task.done():
                task.cancel()
            await asyncio.gather(task, return_exceptions=True)

        if failure:
            raise failure[0]

    def _graph_input(self, frame: TranscriptFinalFrame) -> dict[str, Any]:
        """Map a committed transcript onto ``AgentInputState``."""
        session = self._session
        return {
            "signed_tenant_context": session.signed_tenant_context,
            "session_id": session.session_id,
            "source": {"channel": session.channel, "language": session.language},
            "transcript_text": frame.text,
            "transcript_is_final": True,
            "current_turn_id": frame.turn_id,
        }

    # -- frame builders -------------------------------------------------------

    def _envelope(self, frame: TranscriptFinalFrame) -> dict[str, Any]:
        """Shared envelope fields for every frame this port emits.

        ``seq`` is a placeholder: the pipeline restamps it from the session's
        shared ``TurnManager`` before the frame goes on the wire, which is the
        only sequencer that sees every frame from every source.
        """
        return {
            "tenant_id": frame.tenant_id or self._session.tenant_id,
            "session_id": frame.session_id,
            "turn_id": frame.turn_id,
            "correlation_id": frame.correlation_id or self._session.correlation_id,
            "causation_id": frame.causation_id,
            "seq": 0,
            "ts": datetime.now(UTC),
        }

    def _delta(self, frame: TranscriptFinalFrame, text: str) -> AgentTextDeltaFrame:
        return AgentTextDeltaFrame(
            type=StreamFrameType.AGENT_TEXT_DELTA,
            delta=text,
            **self._envelope(frame),
        )

    def _final(self, frame: TranscriptFinalFrame, text: str) -> AgentTextFinalFrame:
        return AgentTextFinalFrame(
            type=StreamFrameType.AGENT_TEXT_FINAL,
            text=text,
            **self._envelope(frame),
        )

    def _progress(
        self, frame: TranscriptFinalFrame, node: str, started_at: float
    ) -> ToolProgressFrame:
        elapsed_ms = max(0, int((time.monotonic() - started_at) * 1000))
        return ToolProgressFrame(
            type=StreamFrameType.TOOL_PROGRESS,
            tool_name=SLOW_NODES.get(node, node),
            phase=node,
            elapsed_ms=elapsed_ms,
            **self._envelope(frame),
        )
