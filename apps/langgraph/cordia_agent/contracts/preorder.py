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

from typing import TYPE_CHECKING

from pydantic import BaseModel, Field


if TYPE_CHECKING:
    from collections.abc import Callable

    from cordia_agent.contracts.catalog import Money


class PreorderLineReference(BaseModel):
    """One immutable pre-order line (spec 11 §4.3, §21.3)."""

    model_config = {"extra": "forbid"}

    item_id: str = Field(description="Stable opaque catalog item id.")
    modifier_ids: list[str] = Field(
        default_factory=list,
        description="Stable modifier ids selected on this line; empty ⇒ none.",
    )
    quantity: int = Field(ge=1, description="Number of this item ordered (≥ 1).")
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


class PreorderDraftLine(BaseModel):
    """One MUTABLE, in-dialog pre-order line (spec 11 §4.3).

    Unlike :class:`PreorderLineReference` this carries NO captured price — a draft line is assembled
    while the customer is still choosing, and the price is only captured (from the live published
    catalog) when the draft is frozen into an immutable :class:`PreorderReference` at confirmation.
    """

    model_config = {"extra": "forbid"}

    item_id: str = Field(description="Stable opaque catalog item id.")
    modifier_ids: list[str] = Field(
        default_factory=list,
        description="Stable modifier ids selected on this line; empty ⇒ none.",
    )
    quantity: int = Field(ge=1, description="Number of this item ordered (≥ 1).")
    notes: str | None = Field(
        default=None, description="Optional allergen / preparation notes."
    )


class PreorderDraft(BaseModel):
    """The MUTABLE, in-dialog pre-order draft assembled inside the foreground ``BookingDraft``
    (spec 11 §4.3, §2 — the agent only *drafts*).

    The agent builds this up turn by turn from the published-catalog read tools; it holds no prices
    and is never itself the booking side effect. At confirmation it is frozen into the immutable
    :class:`PreorderReference` (via :meth:`to_reference`), which is what gets embedded in the confirmed
    intent revision and folded into the canonical request hash (Inv-1/Inv-2). The draft is foreground
    state only — it never crosses into a durable side effect on its own.
    """

    model_config = {"extra": "forbid"}

    catalog_version: str = Field(
        description="Published menu version the draft is being built against."
    )
    lines: list[PreorderDraftLine] = Field(
        default_factory=list,
        description="In-progress pre-order lines (mutable during the dialog).",
    )

    def to_reference(
        self,
        confirmation_id: str,
        price_lookup: Callable[[str], Money],
    ) -> PreorderReference:
        """Freeze this mutable draft into an immutable :class:`PreorderReference` at confirmation.

        Captures the per-unit price + currency for each line from ``price_lookup`` (a lookup over the
        live published catalog, e.g. backed by ``get_catalog_item``), so the reference records the
        price quoted at confirmation (§21.3). Raises ``ValueError`` for an empty draft — an empty
        pre-order is "no pre-order", never a zero-line reference.

        Args:
            confirmation_id: Customer confirmation evidence id to stamp on the reference (§21.3).
            price_lookup: Maps an ``item_id`` to its per-unit :class:`Money` at confirmation time.

        Returns:
            An immutable :class:`PreorderReference` mirroring this draft with captured prices.
        """
        if not self.lines:
            raise ValueError("Cannot freeze an empty pre-order draft into a reference.")

        lines = [
            PreorderLineReference(
                item_id=line.item_id,
                modifier_ids=list(line.modifier_ids),
                quantity=line.quantity,
                captured_price_amount_minor=(
                    money := price_lookup(line.item_id)
                ).amount_minor,
                captured_price_currency=money.currency,
                notes=line.notes,
            )
            for line in self.lines
        ]
        return PreorderReference(
            catalog_version=self.catalog_version,
            lines=lines,
            confirmation_id=confirmation_id,
        )
