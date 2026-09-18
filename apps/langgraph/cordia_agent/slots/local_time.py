"""Re-anchor a spoken time to the venue's wall clock.

The extractor is asked for the time the CUSTOMER said and returns it tagged UTC:
"seven pm" comes back as ``19:00+00:00``.  That tag is not a claim about the
zone — the model has no idea where the venue is — it is the only thing a naive
wall clock can be serialised as.  Taken literally it books 19:00 UTC, which is
5am in Melbourne: outside every sitting, and read back to the customer as the
time they asked for.

Nothing caught it because the seeded capacity used to have a slot at every hour
of the day, so any instant matched something and the booking looked fine.

This module is the one place that decision is made, and it is deliberately dumb:
take the wall clock the extractor reported, assert it in the venue's zone, and
return the instant that actually is.  ``19:00`` spoken in ``Australia/Melbourne``
becomes ``09:00Z``, which is both the correct instant AND the offset the capacity
slot keys are written at (spec 07 open-decision §4 — the key is offset-sensitive,
so a draft carrying +10:00 would miss a row stored at +00:00).
"""

from __future__ import annotations

import logging
from typing import TYPE_CHECKING
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError


if TYPE_CHECKING:
    from datetime import datetime


_log = logging.getLogger(__name__)


def reanchor_to_venue(spoken: datetime, timezone: str | None) -> datetime:
    """Read *spoken*'s wall clock as a time in *timezone* and return it in UTC.

    Args:
        spoken: What the extractor reported, tagged UTC by convention.
        timezone: The venue's IANA zone, or ``None`` when unknown.

    Returns:
        The same wall clock expressed as an instant in the venue's zone,
        normalised to UTC.  Returns *spoken* unchanged when the zone is unknown
        or unrecognised: guessing a zone books a real table at a wrong hour,
        whereas leaving it alone preserves today's behaviour and stays visible.
    """
    if not timezone:
        _log.debug("no venue timezone on state; leaving %s as spoken", spoken)
        return spoken

    try:
        venue = ZoneInfo(timezone)
    except (ZoneInfoNotFoundError, ValueError):
        _log.warning(
            "unrecognised venue timezone %r; leaving the time as spoken", timezone
        )
        return spoken

    # Drop the extractor's placeholder tag, re-assert the same clock face in the
    # venue's zone, then normalise.  DST is ZoneInfo's problem, which is the
    # entire reason this is not an offset subtraction.
    local = spoken.replace(tzinfo=venue)
    return local.astimezone(ZoneInfo("UTC"))
