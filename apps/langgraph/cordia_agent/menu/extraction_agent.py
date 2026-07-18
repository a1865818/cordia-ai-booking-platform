"""Isolated menu-extraction agent (spec 11 §1b, §3; tests §27.1/§27.6).

Turns an uploaded menu artifact's text into DRAFT catalog items. It is sandboxed
**by construction**:

* It holds an EMPTY :class:`ToolAllowlist` and binds NO tools — there is no code
  path to add one (Inv-12 / §27.1). It therefore cannot publish a catalog, call
  booking/CRM/notification/payment tools, or take any privileged action.
* It treats the menu text strictly as ``TrustLevel.UNTRUSTED`` data (§8.11): any
  "instructions" embedded in the document have no effect.
* Uncertain extractions (low confidence) and injection-like content are FLAGGED
  for owner review, never silently published (§27.6). The agent can only reach the
  ``Extracting → DraftReady`` step; it can never approve or publish.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from pydantic import BaseModel, Field

from cordia_agent.contracts.catalog import CatalogItem
from cordia_agent.safety.injection_harness import ToolAllowlist, label_untrusted
from cordia_agent.security.channels import PromptChannels


if TYPE_CHECKING:
    from cordia_agent.menu.model_port import ExtractionModel


# Phrases that suggest the document is trying to inject instructions. Their presence
# lowers trust and FLAGS the result for owner review — it never changes behavior
# (the empty tool surface already guarantees no privileged action can occur).
_INJECTION_MARKERS: tuple[str, ...] = (
    "ignore all",
    "ignore previous",
    "ignore the above",
    "disregard",
    "you are now",
    "system prompt",
    "publish_vendor_catalog",
    "mark every item free",
    "mark all items free",
)

DEFAULT_CONFIDENCE_THRESHOLD = 0.7


class ExtractionFlag(BaseModel):
    """A single owner-review flag raised during extraction (§27.6)."""

    model_config = {"extra": "forbid"}

    item_id: str | None = Field(
        default=None, description="Item the flag relates to; None for document-level."
    )
    reason: str = Field(description="Why review is needed, e.g. 'low_confidence'.")


class ExtractionResult(BaseModel):
    """Draft items plus any owner-review flags (spec 11 §3).

    Exposes ONLY ``items`` and ``flags`` — there is no field that can carry a
    privileged action (publish/approve), so nothing the agent returns can mutate
    catalog state.
    """

    model_config = {"extra": "forbid"}

    items: list[CatalogItem] = Field(
        description="Draft catalog items (never published)."
    )
    flags: list[ExtractionFlag] = Field(
        default_factory=list, description="Owner-review flags; nothing auto-publishes."
    )


class IsolatedExtractionAgent:
    """Sandboxed menu-extraction agent — empty tool surface by construction (§27.1)."""

    def __init__(
        self,
        model: ExtractionModel,
        *,
        confidence_threshold: float = DEFAULT_CONFIDENCE_THRESHOLD,
    ) -> None:
        self._model = model
        self._confidence_threshold = confidence_threshold
        # Inv-12: empty allowlist, no tools bound. There is no setter — the surface
        # cannot be expanded at runtime.
        self._allowlist = ToolAllowlist([])

    @property
    def allowlist(self) -> ToolAllowlist:
        """The agent's (empty) tool allowlist — exposed for boundary assertions."""
        return self._allowlist

    async def extract(self, menu_text: str) -> ExtractionResult:
        """Extract draft catalog items from untrusted *menu_text*.

        The text is wrapped as ``UNTRUSTED`` and assembled through
        ``PromptChannels`` (spec 14 §8.11) before it reaches the model — this
        agent's system channel is EMPTY today, and the channel constructor
        guarantees the untrusted span can never masquerade as system content.
        Low-confidence items and injection-like content are flagged for owner
        review. No tool is ever invoked and nothing is published.

        Args:
            menu_text: Raw artifact text. Treated strictly as untrusted data.

        Returns:
            An :class:`ExtractionResult` of draft items + owner-review flags.
        """
        # Channel-separated prompt assembly (§8.11): the menu text rides in the
        # untrusted channel ONLY; construction validates trust-homogeneity.
        channels = PromptChannels(untrusted=[label_untrusted(menu_text)])
        items = await self._model.extract(channels.untrusted[0])

        flags: list[ExtractionFlag] = []

        # Defense-in-depth: flag (do NOT act on) injection-like content (§27.6).
        lowered = menu_text.lower()
        if any(marker in lowered for marker in _INJECTION_MARKERS):
            flags.append(ExtractionFlag(reason="suspicious_content"))

        # Flag low-confidence items for owner review (§27.6 / FM#6).
        flags.extend(
            ExtractionFlag(item_id=item.item_id, reason="low_confidence")
            for item in items
            if item.extraction_confidence is not None
            and item.extraction_confidence < self._confidence_threshold
        )

        return ExtractionResult(items=items, flags=flags)
