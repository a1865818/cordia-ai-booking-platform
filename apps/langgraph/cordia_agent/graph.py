"""The Cordia hello graph — a minimal LangGraph workflow.

A single-node graph that renders a greeting. Serves as the working template for
real graphs and exercises the Pydantic -> JSON Schema -> C#/TS type pipeline.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from langgraph.graph import END, START, StateGraph
from pydantic import BaseModel

from cordia_agent.configurations import HelloConfiguration
from cordia_agent.state import GreetingStatus, HelloInputState, HelloOutputState


if TYPE_CHECKING:
    from langgraph.runtime import Runtime


class _HelloState(BaseModel):
    """Internal graph state: input fields plus defaulted output fields.

    Using a state with defaults (rather than the output schema directly) lets
    LangGraph coerce the input — which only carries ``name`` — without tripping
    required-field validation on the output fields.
    """

    name: str = ""
    message: str = ""
    status: GreetingStatus = GreetingStatus.PENDING


async def greet(state: _HelloState, runtime: Runtime[HelloConfiguration]) -> dict:
    """Render a greeting for the given name."""
    greeting = runtime.context.greeting if runtime.context else "Hello"
    return {
        "message": f"{greeting}, {state.name}!",
        "status": GreetingStatus.GREETED,
    }


def build_hello_graph() -> StateGraph:
    """Construct and compile the hello graph."""
    builder = StateGraph(
        _HelloState,
        input_schema=HelloInputState,
        output_schema=HelloOutputState,
        context_schema=HelloConfiguration,
    )
    builder.add_node("greet", greet)
    builder.add_edge(START, "greet")
    builder.add_edge("greet", END)
    return builder.compile()


hello_graph = build_hello_graph()
