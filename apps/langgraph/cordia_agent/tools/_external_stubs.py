"""Re-export seam for models owned by other spec tracks (03-B).

This module is the single canonical import point for cross-track models that
tool contracts depend on but do NOT own:

  * ``BookingDraft``   — owned by spec 02 (cordia_agent.state)
  * ``Confirmation``   — owned by spec 02 (cordia_agent.models.intent)
  * ``IntentRevision`` — owned by spec 02 (cordia_agent.models.intent)

Rationale: tool contract code (spec 03) imports from here rather than reaching
directly into spec-02 modules. This seam makes cross-track dependencies visible
and easy to swap during parallel development.

IMPORTANT: Do NOT declare duplicate model classes here.  Spec 02 has landed on
this branch and the canonical models are already registered in the schema
exporter.  Re-declaring them would produce duplicate-title entries and cause
``export_pydantic_schemas.validate_no_duplicates`` to raise SystemExit.

Placeholder for spec-04 model (not yet available):
  BookingIntent — add re-export here once 04-owned module exists.
"""

from __future__ import annotations

from cordia_agent.models.intent import Confirmation as Confirmation
from cordia_agent.models.intent import IntentRevision as IntentRevision
from cordia_agent.state import BookingDraft as BookingDraft


__all__ = ["BookingDraft", "Confirmation", "IntentRevision"]
