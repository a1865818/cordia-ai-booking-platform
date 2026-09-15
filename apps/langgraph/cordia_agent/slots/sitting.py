"""Work out WHICH sitting a spoken time falls in, so nobody has to ask.

A caller ringing a restaurant asks for a table at seven. They do not know the
venue calls that "the dinner sitting", and they should never be made to: asking
"which service would you like?" is a data model leaking into a phone call.

But the booking needs a sitting, because that is what capacity is keyed on —
``CapacitySlotKey.Derive`` prefers ``resource_id``, then ``service_id``, then
``location_id``, and the venue's covers are per sitting (24 breakfast, 40 lunch,
60 dinner). So the sitting has to be determined; it just does not have to be
*requested*.

How it is determined
    By asking the capacity system, not by hardcoding hours.  Each declared
    service is probed at the requested instant, and the one with TRACKED
    inventory wins.  Tracked is the whole signal: ``check_candidate_availability``
    answers ``live``/``held`` when a real capacity row matches the derived slot
    key, and falls back to ``informational`` when the tenant simply has no
    inventory for that resource at that time.  So "which sitting is 7pm?" is
    answered by "the one that has a row at 7pm", which is the venue's own data
    rather than this module's opinion of when dinner starts.

What it refuses to do
    Guess.  Zero tracked matches means the venue is not serving then, and more
    than one means the sittings genuinely overlap — both are questions for the
    customer, and both leave ``service_id`` unset so ``clarify`` asks, now with
    the options read out.  A sitting picked by coin toss books a real table in
    the wrong room at the wrong covers.

Why the answer is not just ``None``
    Those two are not the same answer, and neither is "the lookup failed".  A
    bare ``None`` collapsed all three, so "a table at 3am" and "the availability
    service is down" reached the caller as the identical next question: which
    room would you like?  Nobody ringing at 3am wants to pick a room; they want
    to be told we are shut.  :class:`SittingResult` keeps the three apart so the
    caller can say the right thing about each.
"""

from __future__ import annotations

import asyncio
import logging
from dataclasses import dataclass
from enum import Enum
from typing import TYPE_CHECKING

from cordia_agent.slots.capture import SlotCapture
from cordia_agent.state import CaptureMethod
from cordia_agent.tools.client_binding import (
    evaluate_candidates,
    parse_availability,
    parse_resolved_resources,
    resolve_tenant_resources_call,
)
from cordia_agent.tools.contracts import AvailabilityRequest
from cordia_agent.tools.status_codes import AvailabilityMode


if TYPE_CHECKING:
    from cordia_agent.state import BookingDraft


_log = logging.getLogger(__name__)

#: Modes that mean "the tenant has real inventory for this resource at this time".
#: ``informational`` explicitly does NOT: it is the answer when no capacity row
#: matched, which is exactly how a wrong sitting would slip through.
TRACKED_MODES: frozenset[AvailabilityMode] = frozenset(
    {AvailabilityMode.LIVE, AvailabilityMode.HELD}
)


class SittingOutcome(str, Enum):
    """Why :func:`derive_sitting` did or did not name a sitting."""

    #: Exactly one sitting has tracked capacity at the requested time.
    derived = "derived"
    #: The venue declares sittings, and NONE of them covers that time.
    outside_service = "outside_service"
    #: Several sittings cover it; only the customer can choose.
    ambiguous = "ambiguous"
    #: Nothing could be established: no declared services, or a probe failed.
    #: Explicitly NOT "we are shut" — an unreachable tool is not a closed venue.
    unavailable = "unavailable"


@dataclass(frozen=True)
class SittingResult:
    """What the probe learned about the draft's time."""

    outcome: SittingOutcome
    #: The ``service_id`` capture to apply; only set for ``derived``.
    capture: SlotCapture | None = None
    #: Names of every sitting the tenant declares, for saying out loud.  Empty
    #: when nothing was declared or the lookup failed, which is precisely when
    #: naming one would be an invention.
    service_names: tuple[str, ...] = ()


async def _has_tracked_capacity(draft: BookingDraft, service_id: str) -> bool | None:
    """Return whether *service_id* has real inventory at the draft's time.

    ``None`` when the probe itself did not answer.  Folding that into ``False``
    made an outage indistinguishable from a closed kitchen, and the agent then
    told callers it was not serving when it had simply failed to ask.
    """
    probe = draft.model_copy(
        update={"request": draft.request.model_copy(update={"service_id": service_id})}
    )
    result = parse_availability(
        await evaluate_candidates(AvailabilityRequest(draft=probe, want_hold=False))
    )
    if result is None:
        return None
    return result.mode in TRACKED_MODES


async def derive_sitting(draft: BookingDraft) -> SittingResult:
    """Work out which sitting the draft's time falls in.

    Args:
        draft: The draft, which must already carry a concrete ``starts_at``.

    Returns:
        A :class:`SittingResult`.  ``derived`` carries the capture to apply,
        with ``resolved_name`` set so read-back can say "Dinner sitting";
        ``outside_service`` and ``ambiguous`` carry the declared sitting names
        so the caller can be offered them; ``unavailable`` carries nothing,
        because nothing was established.
    """
    view = parse_resolved_resources(await resolve_tenant_resources_call(kind="service"))
    if view is None or not view.matches:
        _log.info("no declared services to derive a sitting from")
        return SittingResult(SittingOutcome.unavailable)

    names = tuple(m.name for m in view.matches)

    # Probed concurrently: this runs inside a caller's turn, and three sequential
    # round trips at the §4.3 p95 of 150 ms each is half a second of silence.
    tracked = await asyncio.gather(
        *(_has_tracked_capacity(draft, m.resource_id) for m in view.matches)
    )

    if any(ok is None for ok in tracked):
        _log.warning(
            "a capacity probe did not answer for %s; not claiming we are shut",
            draft.request.starts_at,
        )
        return SittingResult(SittingOutcome.unavailable, service_names=names)

    hits = [m for m, ok in zip(view.matches, tracked, strict=True) if ok]

    if not hits:
        _log.info(
            "no sitting covers %s; the venue is not serving then",
            draft.request.starts_at,
        )
        return SittingResult(SittingOutcome.outside_service, service_names=names)

    if len(hits) > 1:
        _log.info(
            "%d sittings cover %s; leaving service unset so clarify asks",
            len(hits),
            draft.request.starts_at,
        )
        return SittingResult(SittingOutcome.ambiguous, service_names=names)

    hit = hits[0]
    _log.debug("derived sitting %s for %s", hit.resource_id, draft.request.starts_at)
    return SittingResult(
        SittingOutcome.derived,
        capture=SlotCapture(
            field="service_id",
            value=hit.resource_id,
            capture_method=CaptureMethod.tool_selection,
            normalized_value=hit.resource_id,
            resolved_name=hit.name,
        ),
        service_names=names,
    )
