"""Unit tests for the AvailabilityMode mirror import.

Note: spec 07 owns the semantics of AvailabilityMode. This module only verifies
that the re-export in cordia_agent.models.availability is the same object as the
canonical enum in cordia_agent.tools.status_codes (no duplication).
"""

from __future__ import annotations

import pytest

from cordia_agent.models.availability import AvailabilityMode as MirrorAvailabilityMode
from cordia_agent.tools.status_codes import (
    AvailabilityMode as CanonicalAvailabilityMode,
)


@pytest.mark.unit
def test_availability_mode_value_set() -> None:
    """AvailabilityMode must expose exactly the four spec-07 values."""
    assert {m.value for m in MirrorAvailabilityMode} == {
        "informational",
        "live",
        "held",
        "confirmed",
    }


@pytest.mark.unit
def test_availability_mode_is_same_object() -> None:
    """Mirror import must be identical to the canonical object (no redefinition)."""
    assert MirrorAvailabilityMode is CanonicalAvailabilityMode
