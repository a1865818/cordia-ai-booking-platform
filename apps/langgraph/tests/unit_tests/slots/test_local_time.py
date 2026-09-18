"""Unit tests for reading a spoken time on the venue's clock."""

from __future__ import annotations

from datetime import UTC, datetime

import pytest

from cordia_agent.slots.local_time import reanchor_to_venue


@pytest.mark.unit
def test_seven_pm_means_seven_pm_where_the_venue_is() -> None:
    """The bug this exists for: "seven pm" was stored as 19:00Z, which is 5am in
    Melbourne — outside every sitting, and read back as the time requested."""
    spoken = datetime(2026, 9, 14, 19, 0, tzinfo=UTC)

    assert reanchor_to_venue(spoken, "Australia/Melbourne") == datetime(
        2026, 9, 14, 9, 0, tzinfo=UTC
    )


@pytest.mark.unit
def test_the_result_is_utc_so_it_matches_the_capacity_key() -> None:
    """Slot keys are offset-sensitive (spec 07 §4): a draft carrying +10:00 misses
    a row written at +00:00, silently falling back to the informational path."""
    out = reanchor_to_venue(
        datetime(2026, 9, 14, 19, 0, tzinfo=UTC), "Australia/Melbourne"
    )

    assert out.utcoffset().total_seconds() == 0


@pytest.mark.unit
def test_daylight_saving_is_not_a_fixed_offset() -> None:
    """January is AEDT (+11), September is AEST (+10). Subtracting a constant
    would be an hour wrong for half the year."""
    january = reanchor_to_venue(
        datetime(2027, 1, 14, 19, 0, tzinfo=UTC), "Australia/Melbourne"
    )
    september = reanchor_to_venue(
        datetime(2026, 9, 14, 19, 0, tzinfo=UTC), "Australia/Melbourne"
    )

    assert january.hour == 8
    assert september.hour == 9


@pytest.mark.unit
@pytest.mark.parametrize("tz", [None, "", "Not/AZone"])
def test_an_unknown_zone_changes_nothing(tz: str | None) -> None:
    """Guessing a zone books a real table at the wrong hour. Leaving it alone
    preserves the previous behaviour and stays visible."""
    spoken = datetime(2026, 9, 14, 19, 0, tzinfo=UTC)

    assert reanchor_to_venue(spoken, tz) == spoken
