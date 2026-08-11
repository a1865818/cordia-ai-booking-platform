"""Node: capture_fields — §8.4 Field Capture Loop.

Drives the slot-filling dialogue: extracts slots from the current transcript
via the :class:`~cordia_agent.slots.capture.SlotExtractor` port, applies them
to the draft via the constrained capture engine, and routes to clarify /
verify_identity / check_candidates / read_back once the draft is ready.

Invariant Inv-1 is enforced inside :func:`~cordia_agent.slots.capture.capture_slots`:
partial transcripts (``transcript_is_final=False``) leave the draft unchanged.

Material updates are persisted immediately via the module-default
:class:`~cordia_agent.checkpoint.SessionCheckpointer` (§8.9).
"""

from __future__ import annotations

from cordia_agent.checkpoint import get_session_checkpointer
from cordia_agent.slots.capture import (
    capture_slots,
    get_slot_extractor,
    required_candidate_fields_present,
)
from cordia_agent.state import AgentState, OperationType


async def capture_fields(state: AgentState) -> dict:
    """Extract and apply slot captures from the current transcript (§8.4).

    1. If ``state.draft`` is ``None``, nothing to capture — returns empty dict.
    2. Calls :func:`~cordia_agent.slots.capture.get_slot_extractor` and awaits
       its ``extract`` to obtain
       :class:`~cordia_agent.slots.capture.SlotCapture` objects from
       ``state.transcript_text``.  The port is async because the production
       extractor is model-backed.
    3. Applies captures via :func:`~cordia_agent.slots.capture.capture_slots`
       respecting ``state.transcript_is_final`` (Inv-1).
    4. If the request changed (material update) AND ``tenant_id`` + ``session_id``
       are both set, checkpoints the updated state (§8.9).
    5. Returns the updated draft on state.

    Args:
        state: Current ``AgentState``.

    Returns:
        A partial state-update dict with the (possibly unchanged) ``draft``.
    """
    if state.draft is None:
        return {}

    extractor = get_slot_extractor()
    captures = await extractor.extract(state.transcript_text or "")

    updated_draft = capture_slots(
        state.draft,
        captures,
        source_is_final=state.transcript_is_final,
        source_turn_id=state.current_turn_id,
    )

    # Detect material update by comparing request JSON (byte-stable).
    request_changed = (
        updated_draft.request.model_dump_json() != state.draft.request.model_dump_json()
    )

    result: dict = {"draft": updated_draft}

    if request_changed and state.tenant_id and state.session_id:
        # Build the candidate updated state and checkpoint it.
        updated_state = state.model_copy(update={"draft": updated_draft})
        get_session_checkpointer().checkpoint_material_update(updated_state)

    return result


def route_after_capture_fields(state: AgentState) -> str:
    """Route after the capture loop based on draft completeness and operation (§8.4).

    Routing precedence:
    1. No draft or required candidate fields missing → ``"clarify"``
    2. Mutation operation (modify/cancel/reschedule) without a verification
       grant → ``"verify_identity"``
    3. Create or reschedule with all required fields → ``"check_candidates"``
    4. Otherwise (modify/cancel with a grant, or any other path) → ``"read_back"``

    All four targets are enumerated in the ``path_map`` in ``graph.py`` so
    the compiled graph statically records every edge.

    Args:
        state: Current ``AgentState``.

    Returns:
        One of ``"clarify"``, ``"verify_identity"``, ``"check_candidates"``,
        ``"read_back"``.
    """
    if state.draft is None or not required_candidate_fields_present(state.draft):
        return "clarify"

    # Route from the VALIDATED draft, not state.operation — the draft's request
    # is the authoritative captured state (Inv-1); the two can diverge and the
    # router must follow the draft.
    operation = state.draft.request.operation

    mutation_ops = {
        OperationType.modify,
        OperationType.cancel,
        OperationType.reschedule,
    }
    if operation in mutation_ops and state.verification_grant_id is None:
        return "verify_identity"

    candidate_ops = {OperationType.create, OperationType.reschedule}
    if operation in candidate_ops:
        return "check_candidates"

    return "read_back"
