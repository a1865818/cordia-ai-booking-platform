"""Per-tool status enums and the AvailabilityMode binding (spec 03 §4.3/§4.4).

These ``(str, Enum)`` types are the source of truth for the shared status codes
exported to C# and TypeScript/Zod via ``tools/scripts/export_pydantic_schemas.py``.
Per-tool status enums are added in Phases 2-3.
"""

from __future__ import annotations

from enum import Enum


# AvailabilityMode is OWNED by spec 07 (07-capacity-and-holds.md). It is bound
# here as a frozen value set so the tool contracts can type against it now.
# DO NOT redefine elsewhere: spec 07 must IMPORT this symbol, not re-declare it
# (export_pydantic_schemas.validate_no_duplicates fails on a duplicate title).
class AvailabilityMode(str, Enum):
    """Availability mode bounding agent wording (owned by spec 07)."""

    INFORMATIONAL = "informational"
    LIVE = "live"
    HELD = "held"
    CONFIRMED = "confirmed"
