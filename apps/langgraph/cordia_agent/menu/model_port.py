"""Model port for the isolated menu-extraction agent (spec 11 §3).

The extraction agent depends on this narrow port, never on a concrete LLM, so the
test suite runs fully offline against a deterministic fake (see ``tests/_stubs``)
and a real LLM can be swapped in later behind the same interface (no wiring change
to the agent).

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
