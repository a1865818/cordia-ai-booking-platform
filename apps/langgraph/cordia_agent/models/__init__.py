"""Models package — intent, availability, and handoff models (spec 02)."""

from __future__ import annotations

from cordia_agent.models.availability import AvailabilityMode
from cordia_agent.models.handoff import HandoffPackage
from cordia_agent.models.intent import (
    Confirmation,
    IntentRevision,
    confirmation_binds_revision,
)


__all__ = [
    "AvailabilityMode",
    "Confirmation",
    "HandoffPackage",
    "IntentRevision",
    "confirmation_binds_revision",
]
