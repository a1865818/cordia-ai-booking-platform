"""Unit tests for the §8.3 foreground-agent graph topology.

Verifies that:
  1. The graph compiles without raising.
  2. Every transition in ALLOWED is present in the compiled edge set.
  3. Forbidden transitions are absent.
  4. Both terminal nodes (submit_intent, human_handoff) are reachable from
     START and route to END.
"""

from __future__ import annotations

from collections import deque

import pytest
from langgraph.graph import END, START

from cordia_agent.graph import build_agent_graph


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _edges() -> set[tuple[str, str]]:
    """Return the compiled static edge set as (source, target) pairs."""
    g = build_agent_graph().get_graph()
    return {(e.source, e.target) for e in g.edges}


# §8.3 canonical allowed transitions (using imported START/END constants so
# the test breaks if the sentinel strings ever change).
ALLOWED: set[tuple[str, str]] = {
    (START, "resolve_tenant"),
    ("resolve_tenant", "load_context"),
    ("load_context", "identify_intent"),
    ("identify_intent", "capture_fields"),
    ("capture_fields", "clarify"),
    ("clarify", "capture_fields"),
    ("capture_fields", "verify_identity"),
    ("verify_identity", "capture_fields"),
    ("verify_identity", "human_handoff"),
    ("capture_fields", "check_candidates"),
    ("check_candidates", "capture_fields"),
    ("check_candidates", "read_back"),
    ("capture_fields", "read_back"),
    ("read_back", "await_confirmation"),
    ("await_confirmation", "capture_fields"),
    ("await_confirmation", "submit_intent"),
    ("submit_intent", END),
    ("submit_intent", "capture_fields"),
    ("submit_intent", "human_handoff"),
    ("human_handoff", END),
}

# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_graph_compiles() -> None:
    """Graph builds and compiles without raising."""
    g = build_agent_graph()
    assert g is not None


@pytest.mark.unit
def test_all_allowed_transitions_present() -> None:
    """Every §8.3 transition appears in the compiled edge set."""
    edges = _edges()
    missing = ALLOWED - edges
    assert not missing, f"Missing transitions: {sorted(missing)}"


@pytest.mark.unit
def test_forbidden_transitions_absent() -> None:
    """Transitions that are not in §8.3 must NOT appear in the compiled graph."""
    edges = _edges()
    forbidden = [
        ("resolve_tenant", "submit_intent"),
        ("identify_intent", "read_back"),
        ("load_context", "submit_intent"),
    ]
    present = [t for t in forbidden if t in edges]
    assert not present, f"Forbidden transitions found: {present}"


@pytest.mark.unit
def test_terminals_reachable_from_start_and_route_to_end() -> None:
    """submit_intent and human_handoff are reachable from START and lead to END."""
    edges = _edges()

    # Both terminals must directly link to END.
    assert ("submit_intent", END) in edges, "submit_intent → END missing"
    assert ("human_handoff", END) in edges, "human_handoff → END missing"

    # BFS: verify START-reachable path to both terminals.
    adjacency: dict[str, list[str]] = {}
    for src, tgt in edges:
        adjacency.setdefault(src, []).append(tgt)

    visited: set[str] = set()
    queue: deque[str] = deque([START])
    while queue:
        node = queue.popleft()
        if node in visited:
            continue
        visited.add(node)
        for neighbour in adjacency.get(node, []):
            queue.append(neighbour)

    assert "submit_intent" in visited, "submit_intent not reachable from START"
    assert "human_handoff" in visited, "human_handoff not reachable from START"
