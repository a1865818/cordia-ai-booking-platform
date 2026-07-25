"""The Cordia foreground conversational agent graph (§8.3).

Builds the full node/edge topology from the spec; every node is a pure
pass-through stub so the suite runs fully offline.  Real implementations will
replace the stubs in later tickets.
"""

from __future__ import annotations

from langgraph.graph import END, START, StateGraph

from cordia_agent.nodes import (
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
    route_after_submit_intent,
    route_after_verify_identity,
    submit_intent,
    verify_identity,
)
from cordia_agent.observability.metrics import init_metrics
from cordia_agent.safety.jws_verifier import bootstrap_verifier
from cordia_agent.security.consent import consent_gate, route_after_consent_gate
from cordia_agent.state import AgentInputState, AgentOutputState, AgentState


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


def build_agent_graph() -> StateGraph:
    """Construct and compile the foreground agent graph (§8.3 topology)."""
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
    builder.add_edge("identify_intent", "capture_fields")
    builder.add_edge("clarify", "capture_fields")
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
        },
    )
    builder.add_conditional_edges(
        "check_candidates",
        route_after_check_candidates,
        {
            "capture_fields": "capture_fields",
            "read_back": "read_back",
        },
    )
    builder.add_conditional_edges(
        "await_confirmation",
        route_after_await_confirmation,
        {
            "capture_fields": "capture_fields",
            "submit_intent": "submit_intent",
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

    return builder.compile()


agent_graph = build_agent_graph()
