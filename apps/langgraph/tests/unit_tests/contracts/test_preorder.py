"""Unit tests for the cross-service pre-order contracts (spec 11 §4.3).

The committed ``PreorderReference`` is the immutable, revalidated-before-commit carrier
embedded in a confirmed intent revision (so it is part of the canonical request hash —
Inv-1/Inv-2). It mirrors the spec §4.3 C# record exactly: stable item/modifier ids, the
immutable catalog version, quantity, the captured per-unit price + currency, optional
allergen/prep notes, and the customer confirmation evidence id.
"""

from __future__ import annotations

import pytest
from pydantic import ValidationError

from cordia_agent.contracts.catalog import Money
from cordia_agent.contracts.preorder import (
    PreorderDraft,
    PreorderDraftLine,
    PreorderLineReference,
    PreorderReference,
)


def _line(**overrides: object) -> PreorderLineReference:
    kwargs: dict[str, object] = {
        "item_id": "item_1",
        "modifier_ids": ["mod_1"],
        "quantity": 2,
        "captured_price_amount_minor": 3200,
        "captured_price_currency": "AUD",
    }
    kwargs.update(overrides)
    return PreorderLineReference(**kwargs)


def _ref(**overrides: object) -> PreorderReference:
    kwargs: dict[str, object] = {
        "catalog_version": "menu_2026_06_16",
        "lines": [_line()],
        "confirmation_id": "conf_1",
    }
    kwargs.update(overrides)
    return PreorderReference(**kwargs)


@pytest.mark.unit
def test_line_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        _line(surprise="x")


@pytest.mark.unit
def test_reference_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        _ref(surprise="x")


@pytest.mark.unit
def test_line_defaults() -> None:
    """modifier_ids defaults to empty; notes defaults to None."""
    line = PreorderLineReference(
        item_id="item_1",
        quantity=1,
        captured_price_amount_minor=1000,
        captured_price_currency="AUD",
    )
    assert line.modifier_ids == []
    assert line.notes is None


@pytest.mark.unit
def test_line_subtotal_is_unit_price_times_quantity() -> None:
    line = _line(quantity=3, captured_price_amount_minor=500)
    assert line.line_subtotal == 1500


@pytest.mark.unit
def test_reference_total_is_sum_of_line_subtotals() -> None:
    ref = _ref(
        lines=[
            _line(quantity=2, captured_price_amount_minor=500),  # 1000
            _line(item_id="item_2", quantity=1, captured_price_amount_minor=750),  # 750
        ]
    )
    assert ref.total_amount_minor == 1750


@pytest.mark.unit
def test_reference_round_trip() -> None:
    ref = _ref(
        lines=[
            _line(notes="no peanuts"),
            _line(item_id="item_2", modifier_ids=[], quantity=1),
        ]
    )
    restored = PreorderReference.model_validate(ref.model_dump())
    assert restored == ref
    assert restored.catalog_version == "menu_2026_06_16"
    assert restored.confirmation_id == "conf_1"
    assert restored.lines[0].notes == "no peanuts"


@pytest.mark.unit
def test_reference_serializes_without_computed_helpers() -> None:
    """line_subtotal / total_amount_minor are derived, not serialized contract fields."""
    dumped = _ref().model_dump()
    assert "total_amount_minor" not in dumped
    assert "line_subtotal" not in dumped["lines"][0]


# ── Mutable draft (spec 11 §4.3) ──────────────────────────────────────────────


def _draft(**overrides: object) -> PreorderDraft:
    kwargs: dict[str, object] = {
        "catalog_version": "menu_2026_06_16",
        "lines": [PreorderDraftLine(item_id="item_1", quantity=2, notes="no peanuts")],
    }
    kwargs.update(overrides)
    return PreorderDraft(**kwargs)


@pytest.mark.unit
def test_draft_line_defaults() -> None:
    """A draft line carries NO price; modifier_ids defaults empty, notes None."""
    line = PreorderDraftLine(item_id="item_1", quantity=1)
    assert line.modifier_ids == []
    assert line.notes is None
    # Draft lines never carry a captured price (that is captured at freeze time only).
    assert "captured_price_amount_minor" not in line.model_dump()


@pytest.mark.unit
def test_draft_line_rejects_zero_quantity() -> None:
    with pytest.raises(ValidationError):
        PreorderDraftLine(item_id="item_1", quantity=0)


@pytest.mark.unit
def test_draft_rejects_extra_fields() -> None:
    with pytest.raises(ValidationError):
        _draft(surprise="x")


@pytest.mark.unit
def test_draft_freezes_to_reference_capturing_prices() -> None:
    """to_reference captures per-unit price + currency from the lookup and mirrors the draft."""
    prices = {
        "item_1": Money(amount_minor=3200, currency="AUD"),
        "item_2": Money(amount_minor=750, currency="AUD"),
    }
    draft = _draft(
        lines=[
            PreorderDraftLine(
                item_id="item_1", modifier_ids=["mod_1"], quantity=2, notes="no peanuts"
            ),
            PreorderDraftLine(item_id="item_2", quantity=1),
        ]
    )

    ref = draft.to_reference("conf_9", price_lookup=lambda item_id: prices[item_id])

    assert isinstance(ref, PreorderReference)
    assert ref.catalog_version == "menu_2026_06_16"
    assert ref.confirmation_id == "conf_9"
    assert [line.item_id for line in ref.lines] == ["item_1", "item_2"]
    assert ref.lines[0].captured_price_amount_minor == 3200
    assert ref.lines[0].captured_price_currency == "AUD"
    assert ref.lines[0].modifier_ids == ["mod_1"]
    assert ref.lines[0].notes == "no peanuts"
    # Captured prices flow into the derived total: 3200*2 + 750*1.
    assert ref.total_amount_minor == 7150


@pytest.mark.unit
def test_empty_draft_cannot_be_frozen() -> None:
    with pytest.raises(ValueError):
        _draft(lines=[]).to_reference(
            "conf_1", price_lookup=lambda _: Money(amount_minor=1, currency="AUD")
        )
