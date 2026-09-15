"""The Cordia foreground conversational agent graph (§8.3).

Builds the full node/edge topology from the spec and wires the four env-guarded
production bootstraps (metrics, the TenantContext verifier, the ToolClient, the
slot extractor).

The per-turn boundary
    One customer utterance is one graph invocation, and the graph ENDs when the
    agent has said something and needs an answer.  There are five such points:

    - ``consent_gate`` → END while the recording notice is unanswered;
    - ``clarify`` → END, having asked for a field or offered another time;
    - ``await_confirmation`` → END when the read-back was not confirmed on
      THIS turn;
    - ``verify_identity`` → END when the grant expired and a fresh code has to
      come from the customer;
    - ``answer_inquiry`` → END, having answered an informational question.

    The §8.3 drawing had none of these: clarify looped to ``capture_fields``,
    an unconfirmed read-back looped to ``capture_fields``, and a zero-candidate
    availability result looped to ``capture_fields``.  Every one of those loops
    re-ran the extractor over an UNCHANGED transcript, so it produced an
    unchanged draft and came straight back — a cycle terminated only by
    ``GraphRecursionError``.  The text runner survived by breaking out of
    ``astream`` at the confirmation gate; a voice call has no driver able to do
    that, and the caller simply heard silence.  Ending the turn inside the
    graph is what makes both channels run the same topology.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from langgraph.graph import END, START, StateGraph

from cordia_agent.nodes import (
    answer_inquiry,
    await_confirmation,
    capture_fields,
    check_candidates,
    clarify,
    human_handoff,
    identify_intent,
    load_context,
    read_back,
    resolve_tenant,
    route_after_await_confirmation,
    route_after_capture_fields,
    route_after_check_candidates,
    route_after_identify_intent,
    route_after_submit_intent,
    route_after_verify_identity,
    submit_intent,
    verify_identity,
)
from cordia_agent.observability.metrics import init_metrics
from cordia_agent.safety.jws_verifier import bootstrap_verifier
from cordia_agent.security.consent import consent_gate, route_after_consent_gate
from cordia_agent.slots.extractor import bootstrap_slot_extractor
from cordia_agent.state import AgentInputState, AgentOutputState, AgentState
from cordia_agent.tools.client import bootstrap_tool_client


if TYPE_CHECKING:
    from langgraph.checkpoint.base import BaseCheckpointSaver


# Initialise OTel metrics once at module load (graph startup).
# Fail-open: init_metrics() never raises — when OTEL_EXPORTER_OTLP_ENDPOINT
# is not set it returns immediately without attempting a connection (§8).
init_metrics()

# Register the production TenantContext JWS verifier once at graph startup
# (spec-15 Phase 5, Inv-13).  Env-guarded, mirroring init_metrics: a production
# process with CORDIA_TENANT_CONTEXT_SIGNING_KEYS set registers a real verifier
# (crashing at startup on malformed key config — fail closed); without the env
# var the registry stays unset and resolve_tenant fails closed with "not wired".
bootstrap_verifier()

# Register the production ToolClient once at graph startup (spec 03 §3.3).
# Env-guarded exactly like bootstrap_verifier: with CORDIA_API_BASE_URL set the
# process gets a real HTTP-backed client (crashing at startup on a malformed
# URL — fail closed); without it the registry stays unset and every tool call
# keeps raising "no ToolClient configured".  A client already installed by a
# test fixture is never replaced.
bootstrap_tool_client()

# Register the production model-backed SlotExtractor once at graph startup
# (§8.4).  Env-guarded on AZURE_OPENAI_DEPLOYMENT_SLOT_EXTRACTOR: unset ⇒ the
# registry stays empty and capture_fields fails closed with "no slot extractor
# registered", keeping the offline suite model-free.
bootstrap_slot_extractor()


def build_agent_graph(checkpointer: BaseCheckpointSaver | None = None) -> StateGraph:
    """Construct and compile the foreground agent graph (§8.3 topology).

    Args:
        checkpointer: Optional LangGraph checkpointer.  The dialog spans several
            turns (details → read-back → confirmation) and each turn is one
            graph invocation, so a multi-turn caller must supply a checkpointer
            for state to survive between them.  ``None`` (the default, and what
            the module-level ``agent_graph`` uses) compiles a stateless graph.

    Returns:
        The compiled graph.
    """
    builder = StateGraph(
        AgentState,
        input_schema=AgentInputState,
        output_schema=AgentOutputState,
    )

    # ------------------------------------------------------------------ nodes
    builder.add_node("resolve_tenant", resolve_tenant)
    builder.add_node("consent_gate", consent_gate)
    builder.add_node("load_context", load_context)
    builder.add_node("identify_intent", identify_intent)
    builder.add_node("answer_inquiry", answer_inquiry)
    builder.add_node("capture_fields", capture_fields)
    builder.add_node("clarify", clarify)
    builder.add_node("verify_identity", verify_identity)
    builder.add_node("check_candidates", check_candidates)
    builder.add_node("read_back", read_back)
    builder.add_node("await_confirmation", await_confirmation)
    builder.add_node("submit_intent", submit_intent)
    builder.add_node("human_handoff", human_handoff)

    # -------------------------------------------------------- unconditional edges
    builder.add_edge(START, "resolve_tenant")
    # Consent gate (spec 14 §5.1) runs before any transcript-bearing node.
    builder.add_edge("resolve_tenant", "consent_gate")
    builder.add_edge("load_context", "identify_intent")
    # answer_inquiry ENDs the turn — a fifth per-turn boundary, and the same
    # reason as the other four: the agent has answered and is waiting.
    builder.add_edge("answer_inquiry", END)
    # clarify ENDs the turn — see the per-turn boundary note above.  The §8.3
    # drawing had clarify → capture_fields; that edge was a cycle, because
    # capture_fields re-ran the extractor over the transcript that had just
    # failed to yield the field.
    builder.add_edge("clarify", END)
    builder.add_edge("read_back", "await_confirmation")
    builder.add_edge("human_handoff", END)

    # --------------------------------------------------------- conditional edges
    # Each path_map must enumerate ALL allowed targets so get_graph().edges
    # statically records every transition (required for topology tests).
    builder.add_conditional_edges(
        "consent_gate",
        route_after_consent_gate,
        {
            "load_context": "load_context",
            END: END,
        },
    )
    # identify_intent -> capture_fields used to be unconditional, which sent an
    # informational utterance into the capture loop with no draft and dead-ended
    # it (see nodes/answer_inquiry.py).  Booking intents still take that edge.
    builder.add_conditional_edges(
        "identify_intent",
        route_after_identify_intent,
        {
            "answer_inquiry": "answer_inquiry",
            "capture_fields": "capture_fields",
            "verify_identity": "verify_identity",
        },
    )
    builder.add_conditional_edges(
        "capture_fields",
        route_after_capture_fields,
        {
            "clarify": "clarify",
            "verify_identity": "verify_identity",
            "check_candidates": "check_candidates",
            "read_back": "read_back",
        },
    )
    builder.add_conditional_edges(
        "verify_identity",
        route_after_verify_identity,
        {
            "capture_fields": "capture_fields",
            "human_handoff": "human_handoff",
            END: END,
        },
    )
    builder.add_conditional_edges(
        "check_candidates",
        route_after_check_candidates,
        {
            "clarify": "clarify",
            "read_back": "read_back",
        },
    )
    builder.add_conditional_edges(
        "await_confirmation",
        route_after_await_confirmation,
        {
            "submit_intent": "submit_intent",
            END: END,
        },
    )
    builder.add_conditional_edges(
        "submit_intent",
        route_after_submit_intent,
        {
            END: END,
            "capture_fields": "capture_fields",
            "human_handoff": "human_handoff",
        },
    )

    return builder.compile(checkpointer=checkpointer)


agent_graph = build_agent_graph()
