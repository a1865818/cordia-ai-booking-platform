"""Test-only fake chat model for the structured-output call shape.

Stands in for a ``BaseChatModel`` wherever production code does
``model.with_structured_output(Schema, include_raw=True)`` and then invokes the
result through :func:`cordia_agent.llm.invoke.ainvoke_with_timeout` — today the
menu extractor (``cordia_agent/menu/model_azure.py``).  It records the messages
it was handed so a test can assert WHICH channel untrusted text landed in, and
replays a scripted result or error so the suite runs fully offline.

DO NOT import this module from production code.  It must only be referenced
from tests and test fixtures.
"""

from __future__ import annotations

from typing import Any


class FakeStructuredChatModel:
    """Chat model that returns a scripted structured-output payload.

    Acts as its own runnable: :meth:`with_structured_output` returns ``self``,
    so the messages reaching :meth:`ainvoke` are exactly the messages the
    production code assembled.
    """

    def __init__(self, *, result: Any = None, error: BaseException | None = None):
        """Script the fake.

        Args:
            result: The value :meth:`ainvoke` returns.  For ``include_raw=True``
                callers this is a ``{"raw", "parsed", "parsing_error"}`` dict.
            error: When given, :meth:`ainvoke` raises this instead of returning.
        """
        self._result = result
        self._error = error
        self.structured_schema: Any = None
        self.include_raw: bool | None = None
        self.calls = 0
        self.last_messages: Any = None
        self.seen_messages: list[Any] = []

    def with_structured_output(
        self, schema: Any, *, include_raw: bool = False
    ) -> FakeStructuredChatModel:
        """Record the requested schema and return this object as the runnable."""
        self.structured_schema = schema
        self.include_raw = include_raw
        return self

    async def ainvoke(self, input: Any, config: Any = None) -> Any:
        """Record the messages, then replay the scripted error or result."""
        self.calls += 1
        self.last_messages = input
        self.seen_messages.append(input)
        if self._error is not None:
            raise self._error
        return self._result
