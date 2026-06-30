"""Model port for the isolated menu-extraction agent (spec 11 §3).

The extraction agent depends on this narrow port, never on a concrete LLM, so the
test suite runs fully offline with ``FakeExtractionModel`` and a real LLM can be
swapped in later behind the same interface (no wiring change to the agent).

The port receives ``LabeledContent`` carrying ``TrustLevel.UNTRUSTED`` — a menu
artifact's text is ALWAYS untrusted data (§8.11 / Inv-12); no implementation may
treat it as instructions or act on it beyond returning draft items.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Protocol


if TYPE_CHECKING:
    from cordia_agent.contracts.catalog import CatalogItem
    from cordia_agent.safety.injection_harness import LabeledContent


class ExtractionModel(Protocol):
    """Turns untrusted menu text into draft catalog items (no tools, no side effects)."""

    async def extract(self, content: LabeledContent) -> list[CatalogItem]:
        """Return draft ``CatalogItem``s extracted from *content* (untrusted menu text)."""
        ...


class FakeExtractionModel:
    """Deterministic in-memory ``ExtractionModel`` for offline tests.

    Returns a fixed list of draft items regardless of input, and records the trust
    level of the last content it received so tests can assert the untrusted-data
    boundary (§8.11).
    """

    def __init__(self, items: list[CatalogItem]) -> None:
        self._items = items
        self.last_content: LabeledContent | None = None

    async def extract(self, content: LabeledContent) -> list[CatalogItem]:
        """Record the received content's trust level and return the configured items."""
        self.last_content = content
        return list(self._items)
