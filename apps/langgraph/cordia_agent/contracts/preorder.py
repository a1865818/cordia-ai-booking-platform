"""Cross-service pre-order contracts (spec 11 §4.3).

Pydantic is the source of truth; C#/TS/Zod are generated from these models via the type
pipeline (``shared-schemas:generate-types``). ``PreorderReference`` is the IMMUTABLE
reference captured at confirmation and revalidated before commit. It is embedded in the
confirmed intent revision so a pre-order is part of the canonical request hash — changing
any line changes the hash, which forces a new revision (Inv-1/Inv-2). There is no separate
pre-order hash.

This mirrors the spec §4.3 C# record exactly (flat captured-price fields, NOT a nested
``Money``) so the generated ``Cordia.Domain.Generated`` types match the carrier the saga
revalidates and commits. ``line_subtotal`` / ``total_amount_minor`` are derived Python
helpers (computed properties), NOT serialized contract fields, so they never enter the
JSON Schema or the canonical hash.
"""

from __future__ import annotations

from pydantic import BaseModel, Field


class PreorderLineReference(BaseModel):
    """One immutable pre-order line (spec 11 §4.3, §21.3)."""

    model_config = {"extra": "forbid"}

    item_id: str = Field(description="Stable opaque catalog item id.")
    modifier_ids: list[str] = Field(
        default_factory=list,
        description="Stable modifier ids selected on this line; empty ⇒ none.",
    )
    quantity: int = Field(description="Number of this item ordered (≥ 1).")
    captured_price_amount_minor: int = Field(
        description="Per-unit price quoted at confirmation, in ISO-4217 minor units."
    )
    captured_price_currency: str = Field(description="ISO 4217 code, e.g. 'AUD'.")
    notes: str | None = Field(
        default=None, description="Optional allergen / preparation notes."
    )

    @property
    def line_subtotal(self) -> int:
        """Derived: captured per-unit price x quantity (minor units). Not serialized."""
        return self.captured_price_amount_minor * self.quantity


class PreorderReference(BaseModel):
    """Immutable pre-order reference captured at confirmation (spec 11 §4.3).

    Carries the immutable menu version, the ordered lines, and the customer confirmation
    evidence id. Embedded in the confirmed intent revision (part of the canonical hash).
    """

    model_config = {"extra": "forbid"}

    catalog_version: str = Field(description="IMMUTABLE published menu version label.")
    lines: list[PreorderLineReference] = Field(description="Ordered pre-order lines.")
    confirmation_id: str = Field(
        description="Customer confirmation evidence id (§21.3)."
    )

    @property
    def total_amount_minor(self) -> int:
        """Derived: sum of all line subtotals (minor units). Not serialized."""
        return sum(line.line_subtotal for line in self.lines)
