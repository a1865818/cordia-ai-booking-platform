"""Unit tests for the cross-service catalog contracts (spec 11 §4.2)."""

from __future__ import annotations

import pytest
from pydantic import ValidationError

from cordia_agent.contracts.catalog import (
    AvailabilityWindow,
    CatalogItem,
    CatalogModifier,
    Money,
    PublishedCatalog,
)


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
    assert item.duration_minutes is None
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
        duration_minutes=90,
        extraction_confidence=0.82,
        source_location="page_2:line_5",
    )
    restored = CatalogItem.model_validate(item.model_dump())
    assert restored == item
    assert restored.price.amount_minor == 3200
    assert restored.price.currency == "AUD"
    assert restored.duration_minutes == 90


@pytest.mark.unit
def test_catalog_modifier_shape_and_extra_forbidden() -> None:
    mod = CatalogModifier(
        modifier_id="mod_1",
        name="Extra cheese",
        price_delta=Money(amount_minor=150, currency="AUD"),
    )
    assert mod.modifier_id == "mod_1"
    assert mod.price_delta is not None and mod.price_delta.amount_minor == 150
    # price_delta is optional (a free modifier).
    assert CatalogModifier(modifier_id="mod_2", name="No onion").price_delta is None
    with pytest.raises(ValidationError):
        CatalogModifier(modifier_id="m", name="x", surprise="y")


@pytest.mark.unit
def test_availability_window_shape_and_extra_forbidden() -> None:
    window = AvailabilityWindow(
        days_of_week=["mon", "tue"], start_time="06:00", end_time="11:00"
    )
    assert window.days_of_week == ["mon", "tue"]
    # all fields optional (None ⇒ always available).
    empty = AvailabilityWindow()
    assert (
        empty.days_of_week is None
        and empty.start_time is None
        and empty.end_time is None
    )
    with pytest.raises(ValidationError):
        AvailabilityWindow(surprise="y")


@pytest.mark.unit
def test_catalog_item_rich_fields_round_trip() -> None:
    item = _make_item(
        modifiers=[
            CatalogModifier(
                modifier_id="mod_1",
                name="Extra cheese",
                price_delta=Money(amount_minor=150, currency="AUD"),
            ),
        ],
        availability_window=AvailabilityWindow(
            days_of_week=["fri", "sat"], start_time="17:00", end_time="22:00"
        ),
        image_ref="s3://tenant/img/salmon.jpg",
    )
    restored = CatalogItem.model_validate(item.model_dump())
    assert restored == item
    assert (
        restored.modifiers is not None and restored.modifiers[0].modifier_id == "mod_1"
    )
    assert (
        restored.availability_window is not None
        and restored.availability_window.start_time == "17:00"
    )
    assert restored.image_ref == "s3://tenant/img/salmon.jpg"


@pytest.mark.unit
def test_catalog_item_rich_fields_default_none() -> None:
    item = _make_item()
    assert item.modifiers is None
    assert item.availability_window is None
    assert item.image_ref is None
    assert item.duration_minutes is None


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
