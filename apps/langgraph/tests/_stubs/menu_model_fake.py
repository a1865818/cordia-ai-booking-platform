"""Test-only fake for the ``ExtractionModel`` port (spec 11 §3).

Mirrors ``cordia_agent.menu.model_port`` — one stub module per source port
module, so a fake is always found next to the port it satisfies.  Lets the
menu-extraction suite run fully offline with no LLM dependency.

DO NOT import this module from production code.  It must only be referenced from
tests and test fixtures.
"""

from __future__ import annotations

from typing import TYPE_CHECKING


if TYPE_CHECKING:
    from cordia_agent.contracts.catalog import CatalogItem
    from cordia_agent.safety.injection_harness import LabeledContent


class FakeExtractionModel:
    """Deterministic in-memory ``ExtractionModel`` for offline tests.

    Returns a fixed list of draft items regardless of input, and records the trust
    level of the last content it received so tests can assert the untrusted-data
    boundary (§8.11).
    """

    def __init__(self, items: list[CatalogItem]) -> None:
        """Initialise with the fixed draft items to return.

        Args:
            items: The draft catalog items returned from every :meth:`extract` call.
        """
        self._items = items
        self.last_content: LabeledContent | None = None

    async def extract(self, content: LabeledContent) -> list[CatalogItem]:
        """Record the received content's trust level and return the configured items.

        Args:
            content: The untrusted menu text handed to the model port.

        Returns:
            A copy of the configured draft catalog items.
        """
        self.last_content = content
        return list(self._items)
