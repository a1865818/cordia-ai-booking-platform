"""Cross-service catalog contracts (spec 11 §4.2).

Pydantic is the source of truth here; C#/TS/Zod are generated from these models
via the type pipeline (``shared-schemas:generate-types``). ``Money``,
``CatalogItem``, and ``PublishedCatalog`` describe the published-menu shape the
foreground catalog tools return.

The draft-only fields ``extraction_confidence`` / ``source_location`` are
populated ONLY by the isolated extraction agent and are NEVER surfaced to
customers as fact (§4.2); the foreground tool result excludes them.
"""

from __future__ import annotations

from pydantic import BaseModel, Field


class Money(BaseModel):
    """A monetary amount in ISO-4217 minor units (spec 11 §4.2)."""

    model_config = {"extra": "forbid"}

    amount_minor: int = Field(description="Minor units, e.g. 2200 = $22.00.")
    currency: str = Field(description="ISO 4217 code, e.g. 'AUD'.")


class CatalogModifier(BaseModel):
    """A selectable item modifier (spec 11 §4.2, §21.3).

    Carries a stable ``modifier_id`` referenced by ``PreorderDraftLine.modifier_ids`` and
    captured into the immutable ``PreorderReference``. ``price_delta`` is added to the base
    item price when selected; ``None`` means a free modifier.
    """

    model_config = {"extra": "forbid"}

    modifier_id: str = Field(description="Stable opaque modifier id, e.g. 'mod_01...'.")
    name: str = Field(description="Modifier display name, e.g. 'Extra cheese'.")
    price_delta: Money | None = Field(
        default=None, description="Price added when selected; None ⇒ free."
    )


class AvailabilityWindow(BaseModel):
    """When an item is orderable (spec 11 §4.2).

    All fields optional; ``None`` everywhere ⇒ always available. Times are local
    time-of-day (the tenant timezone, resolved from tenant context), 24-hour ``HH:MM``.
    This is descriptive metadata only — fulfillment-time availability is never guaranteed
    by the foreground agent (§21.2); the pre-order saga revalidates before commit.
    """

    model_config = {"extra": "forbid"}

    days_of_week: list[str] | None = Field(
        default=None,
        description="Lowercase day abbreviations, e.g. ['mon','tue']; None ⇒ all days.",
    )
    start_time: str | None = Field(
        default=None, description="Local time-of-day 'HH:MM' (24h); None ⇒ open."
    )
    end_time: str | None = Field(
        default=None, description="Local time-of-day 'HH:MM' (24h); None ⇒ open."
    )


class CatalogItem(BaseModel):
    """A single catalog item (spec 11 §4.2).

    Only ``name``, ``description``, and ``price`` are needed for common
    discovery; the remaining fields are optional. ``extraction_confidence`` and
    ``source_location`` are set ONLY by the isolated extraction agent and are
    excluded from foreground tool results (never customer-facing as fact).
    """

    model_config = {"extra": "forbid"}

    item_id: str = Field(description="Stable opaque item id, e.g. 'item_01...'.")
    name: str = Field(description="Item display name.")
    description: str = Field(description="Item description.")
    price: Money = Field(description="Item price.")
    category: str | None = Field(default=None, description="Optional menu category.")
    modifiers: list[CatalogModifier] | None = Field(
        default=None, description="Optional selectable modifiers (§21.3)."
    )
    allergens: list[str] | None = Field(
        default=None, description="Optional allergen labels."
    )
    dietary_labels: list[str] | None = Field(
        default=None, description="Optional dietary labels (e.g. 'vegan')."
    )
    availability_window: AvailabilityWindow | None = Field(
        default=None, description="Optional orderable window; None ⇒ always available."
    )
    image_ref: str | None = Field(
        default=None,
        description="Optional opaque image reference (e.g. object-store key).",
    )
    extraction_confidence: float | None = Field(
        default=None,
        description="Draft-only: extraction-agent confidence; never customer-facing.",
    )
    source_location: str | None = Field(
        default=None,
        description="Draft-only: artifact source location; never customer-facing.",
    )


class PublishedCatalog(BaseModel):
    """An immutable published catalog version (spec 11 §4.2)."""

    model_config = {"extra": "forbid"}

    catalog_version: str = Field(
        description="Immutable version label, e.g. 'menu_2026_06_16'."
    )
    items: list[CatalogItem] = Field(
        description="Items contained in this published version."
    )
