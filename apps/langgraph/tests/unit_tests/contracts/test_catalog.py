"""Unit tests for the cross-service catalog contracts (spec 11 §4.2)."""

from __future__ import annotations

import pytest
from pydantic import ValidationError

from cordia_agent.contracts.catalog import CatalogItem, Money, PublishedCatalog


def _make_item(**overrides: object) -> CatalogItem:
    """Build a minimal valid CatalogItem."""
    kwargs: dict[str, object] = {
        "item_id": "item_1",
        "name": "Grilled Salmon",
        "description": "Atlantic salmon, lemon butter",
        "price": Money(amount_minor=3200, currency="AUD"),
    }
    kwargs.update(overrides)
    return CatalogItem(**kwargs)


@pytest.mark.unit
def test_money_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        Money(amount_minor=100, currency="AUD", surprise="x")


@pytest.mark.unit
def test_catalog_item_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        _make_item(surprise="x")


@pytest.mark.unit
def test_catalog_item_minimal_discovery_fields() -> None:
    """name/description/price suffice; optional fields default to None."""
    item = _make_item()
    assert item.category is None
    assert item.allergens is None
    assert item.dietary_labels is None
    assert item.extraction_confidence is None
    assert item.source_location is None


@pytest.mark.unit
def test_catalog_item_round_trip() -> None:
    item = _make_item(
        category="mains",
        allergens=["fish", "dairy"],
        dietary_labels=["pescatarian"],
        extraction_confidence=0.82,
        source_location="page_2:line_5",
    )
    restored = CatalogItem.model_validate(item.model_dump())
    assert restored == item
    assert restored.price.amount_minor == 3200
    assert restored.price.currency == "AUD"


@pytest.mark.unit
def test_published_catalog_round_trip() -> None:
    catalog = PublishedCatalog(
        catalog_version="menu_2026_06_16",
        items=[_make_item(), _make_item(item_id="item_2", name="Soup")],
    )
    restored = PublishedCatalog.model_validate(catalog.model_dump())
    assert restored == catalog
    assert restored.catalog_version == "menu_2026_06_16"
    assert len(restored.items) == 2
