"""Unit tests for the hello graph."""

import pytest

from cordia_agent.configurations import HelloConfiguration
from cordia_agent.graph import hello_graph
from cordia_agent.state import GreetingStatus


@pytest.mark.unit
async def test_hello_graph_greets_by_name() -> None:
    result = await hello_graph.ainvoke(
        {"name": "Cordia"},
        context=HelloConfiguration(greeting="Hi"),
    )
    assert result["message"] == "Hi, Cordia!"
    assert result["status"] == GreetingStatus.GREETED


@pytest.mark.unit
async def test_hello_graph_default_greeting() -> None:
    result = await hello_graph.ainvoke({"name": "World"})
    assert result["message"] == "Hello, World!"
