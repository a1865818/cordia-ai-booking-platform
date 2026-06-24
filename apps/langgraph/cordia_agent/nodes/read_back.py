"""Node: read_back — §8.8 Read-Back / Summary.

Renders a human-readable summary of the booking draft and presents it to the
customer for confirmation before submission.  Always advances to
await_confirmation.
"""

from __future__ import annotations

import uuid
from datetime import UTC, datetime
from typing import TYPE_CHECKING

from cordia_agent.readback.renderer import render_read_back
from cordia_agent.revisions.manager import (
    build_revision,
    is_material_change,
    supersede_revision,
)
from cordia_agent.tools.status_codes import AvailabilityMode


if TYPE_CHECKING:
    from cordia_agent.state import AgentState


async def read_back(state: AgentState) -> dict:
    """Render booking draft summary for customer confirmation (§8.8).

    Steps:
    1. If no draft, return empty (guard against null state).
    2. Ensure a current IntentRevision exists — build one if absent, or parse
       the existing one from ``state.current_revision``.
    3. Detect whether the draft's payload has changed since the last revision
       (material change) and supersede if so.
    4. Render via ``render_read_back`` with the effective AvailabilityMode.
    5. Return state update dict with ``read_back_text``, ``read_back_version``,
       ``current_revision``, ``current_intent_id``, and ``read_back_interrupted=False``.
       Clear any stale confirmation when the revision changed.
    """
    if state.draft is None:
        return {}

    from cordia_agent.models.intent import IntentRevision

    draft = state.draft
    req = draft.request
    mode = state.availability_mode or AvailabilityMode.INFORMATIONAL

    now = datetime.now(tz=UTC)

    # ------------------------------------------------------------------ revision
    current_rev: IntentRevision | None = None
    revision_changed = False

    if state.current_revision is None:
        # First time: build a fresh revision
        intent_id = state.current_intent_id or str(uuid.uuid4())
        current_rev = build_revision(
            intent_id=intent_id,
            revision=1,
            canonical_payload=req,
            schema_version=draft.schema_version,
            tenant_policy_version=draft.tenant_policy_version,
            source_session_id=draft.session_id,
            created_at=now,
        )
        revision_changed = True
    else:
        # Parse the stored revision
        current_rev = IntentRevision.model_validate(state.current_revision)

        # Check whether the current draft differs from the stored revision payload
        if is_material_change(current_rev.canonical_payload, req):
            current_rev = supersede_revision(
                current_rev,
                req,
                reason="draft_updated",
                created_at=now,
            )
            revision_changed = True

    # ------------------------------------------------------------------ render
    result = render_read_back(draft, mode)

    update: dict = {
        "read_back_text": result.text,
        "read_back_version": result.read_back_version,
        "current_revision": current_rev.model_dump(mode="json"),
        "current_intent_id": current_rev.intent_id,
        "read_back_interrupted": False,
    }

    # Clear any stale confirmation when the revision changed (Inv-10)
    if revision_changed:
        update["confirmation"] = None

    return update
