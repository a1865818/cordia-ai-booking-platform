"""Node: check_candidates — §8.7 Candidate Availability Check (02-H implementation).

Calls the ``check_candidate_availability`` tool via
``cordia_agent.tools.client_binding.evaluate_candidates`` to fetch concrete
booking slots that satisfy the draft constraints.  Records the availability
mode and candidate data on ``AgentState`` (as cycle-free plain types).

Invariants:
- Inv-16 / §6.6 (degraded handling): when the response status is
  ``available_pending``, ``availability_pending`` is set to ``True`` and the
  mode is NOT treated as held/confirmed/success.  Downstream wording must be
  advisory.  The turn is NEVER blocked.
- §8 checkpoint discipline: this node fires only at a checkpoint (after
  ``route_after_capture_fields`` returned ``"check_candidates"``), i.e.
  when all required fields are present.  ``capture_fields`` itself makes NO
  tool call — that discipline is enforced by the router, not by this node.
- Inv-13: all tool calls are tenant-scoped; ``resolve_tenant`` has already
  verified and set the tenant context on the ``ToolClient``.

Routing:
- ``route_after_check_candidates`` returns ``"clarify"`` when the availability
  result has invalid fields OR no acceptable candidates, and ``"read_back"``
  when at least one candidate is available.  Clarify ends the turn, so a full
  slot asks the customer for another time instead of re-running the extractor
  over an unchanged transcript.

Spec refs: spec 02 §8.7, §6.5, §6.6; spec 03 §4.3; Inv-13, Inv-16.
"""

from __future__ import annotations

import logging

from cordia_agent.state import AgentState  # noqa: TC001
from cordia_agent.tools.contracts import AvailabilityRequest


_log = logging.getLogger(__name__)


async def check_candidates(state: AgentState) -> dict:
    """Check candidate availability for the draft and record results on state (§8.7).

    Steps:
    1. Builds an ``AvailabilityRequest`` from ``state.draft``
       (``want_hold=False`` by default — hold policy TBD for a later ticket).
    2. Calls ``client_binding.evaluate_candidates(req)``; parses the result.
    3. Records on ``AgentState``:
       - ``availability_mode`` — the overall mode from the result.
       - ``availability_candidates`` — serialised candidate dicts (list[dict]).
       - ``availability_invalid_fields`` — invalid draft fields from the result.
       - ``availability_pending`` — True when status == ``"available_pending"``
         (advisory only, Inv-16; NEVER implies held/confirmed/success).
    4. Handles tool-error envelopes gracefully — logs and returns empty candidates
       rather than crashing the graph (§6.6, Inv-16).

    Args:
        state: Current ``AgentState``; ``draft`` must be non-None when this node runs
            (enforced by the router: capture_fields → check_candidates only when
            all required fields are present).

    Returns:
        A partial ``AgentState`` update dict with the availability fields.
    """
    from cordia_agent.tools.client_binding import (
        evaluate_candidates,
        parse_availability,
    )

    if state.draft is None:
        _log.warning("check_candidates called with draft=None; skipping tool call")
        return {}

    req = AvailabilityRequest(
        draft=state.draft,
        want_hold=False,  # Hold policy TBD (later ticket)
    )

    env = await evaluate_candidates(req)

    # Detect available_pending advisory status (Inv-16, §6.6).
    availability_pending = env.status == "available_pending"

    result = parse_availability(env)

    if result is None:
        # Degraded path: tool unavailable or envelope parse failure.
        # Set pending flag when applicable; leave mode/candidates as defaults.
        # Never block the turn (§6.6).
        _log.warning(
            "check_candidates: availability tool returned non-ok or unparseable "
            "response (status=%s); treating as no candidates",
            env.status,
        )
        return {
            "availability_candidates": [],
            "availability_invalid_fields": [],
            "availability_pending": availability_pending,
            "availability_checked": True,
        }

    # ------------------------------------------------------------------
    # Record availability data as cycle-free plain types (state.py constraint).
    # ------------------------------------------------------------------
    candidates_as_dicts = [c.model_dump(mode="json") for c in result.candidates]

    update: dict = {
        "availability_mode": result.mode,
        "availability_candidates": candidates_as_dicts,
        "availability_invalid_fields": result.invalid_fields,
        "availability_pending": availability_pending,
        "availability_checked": True,
    }

    # Inv-16 / §6.6 degraded guard: available_pending MUST NOT be treated as
    # held/confirmed/success.  Log an advisory note so operator tooling can track it.
    if availability_pending:
        _log.info(
            "check_candidates: availability_pending=True — response is advisory only; "
            "mode=%s (Inv-16)",
            result.mode,
        )

    return update


def route_after_check_candidates(state: AgentState) -> str:
    """Route after availability check based on candidate presence and validity (§8.7).

    Returns ``"clarify"`` when:
    - ``availability_invalid_fields`` is non-empty (draft has invalid fields that
      must be corrected before a valid availability lookup is possible), OR
    - ``availability_candidates`` is empty (no acceptable slot found).

    Returns ``"read_back"`` when at least one candidate is available.

    Why ``clarify`` and not ``capture_fields``
        Routing a zero-candidate result back to ``capture_fields`` was a cycle,
        not a recovery: the draft is complete and the transcript is unchanged,
        so the extractor re-ran over the same words, produced the same draft,
        routed back here, and span until ``GraphRecursionError``.  Harmless in a
        script; on a live call the customer got silence where "that time is
        full, how about 7:30?" belonged.  ``clarify`` asks the question and ENDs
        the turn, which is the only thing that can actually unblock a full slot
        — new information from the customer.

    Args:
        state: Current ``AgentState`` after ``check_candidates`` has run.

    Returns:
        ``"clarify"`` or ``"read_back"``.
    """
    if state.availability_invalid_fields:
        return "clarify"
    if not state.availability_candidates:
        return "clarify"
    return "read_back"
