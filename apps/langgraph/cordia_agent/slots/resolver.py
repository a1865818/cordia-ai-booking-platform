"""Trade a spoken name for an identifier the tenant has actually declared.

The slot extractor may never invent an identifier — a hallucinated
``service_id`` would be submitted verbatim, and with the validation stubs still
in DI nothing downstream would refuse it.  That rule is right, and it made a
whole class of perfectly ordinary utterance unusable: "book dinner at the
Fitzroy room" is recognised flawlessly and produces no bookable fields, because
"dinner" is a description and the extractor is forbidden from guessing.

The extractor now reports what the customer CALLED things (``service_name`` /
``location_name``) without pretending to know what they are.  This module is the
step that converts those into identifiers, and it is deliberately the only step
that can:

- The candidate set is the tenant's own ``tenant_resources`` rows, so the
  identifier produced is one the tenant declared, not one a model composed.
- Exactly one match resolves.  Several is a question for the customer, not a
  coin toss, and nothing is captured.
- No match captures nothing, so the draft stays incomplete and ``clarify`` asks.

The failure mode this replaces — a fabricated id sailing through to a booking —
is silent.  The failure mode it introduces is the agent asking a question.
"""

from __future__ import annotations

import logging
from typing import TYPE_CHECKING

from cordia_agent.tools.client_binding import (
    parse_resolved_resources,
    resolve_tenant_resources_call,
)


if TYPE_CHECKING:
    from cordia_agent.slots.capture import SlotCapture


_log = logging.getLogger(__name__)

#: Spoken-name capture field -> (resource kind, the id field it resolves to).
NAME_FIELDS: dict[str, tuple[str, str]] = {
    "service_name": ("service", "service_id"),
    "location_name": ("location", "location_id"),
}


async def resolve_resource_names(captures: list[SlotCapture]) -> list[SlotCapture]:
    """Replace spoken-name captures with resolved identifier captures.

    Args:
        captures: Captures from the extractor, possibly containing
            ``service_name`` / ``location_name``.

    Returns:
        A new list with every name capture removed, plus an id capture for each
        name that resolved to exactly one declared resource, each carrying the
        declared resource's ``name`` on ``resolved_name`` for read-back.  An explicit
        identifier the customer actually spoke always wins: it is left untouched
        and its name counterpart is discarded unresolved.
    """
    names = [c for c in captures if c.field in NAME_FIELDS]
    if not names:
        return captures

    spoken_ids = {c.field for c in captures if c.field.endswith("_id")}
    resolved: list[SlotCapture] = []

    for capture in names:
        kind, id_field = NAME_FIELDS[capture.field]
        if id_field in spoken_ids:
            # The customer gave the identifier outright; a name for the same
            # field is at best a restatement and at worst a disagreement.
            _log.debug(
                "%s already captured explicitly; ignoring %s", id_field, capture.field
            )
            continue

        spoken = str(capture.value).strip()
        if not spoken:
            continue

        view = parse_resolved_resources(
            await resolve_tenant_resources_call(kind=kind, query=spoken)
        )
        if view is None or view.resolved is None:
            _log.info(
                "%s %r did not resolve to a declared %s (status=%s); leaving the "
                "field unset so clarify can ask",
                capture.field,
                spoken,
                kind,
                view.status if view is not None else "unavailable",
            )
            continue

        resolved.append(
            capture.model_copy(
                update={
                    "field": id_field,
                    "value": view.resolved.resource_id,
                    "normalized_value": view.resolved.resource_id,
                    # The identifier is the material term; the name is how it is
                    # said out loud.  Keeping it is the whole difference between
                    # a read-back that says "Fitzroy dining room" and one that
                    # says "fitzroy".
                    "resolved_name": view.resolved.name,
                }
            )
        )

    return [c for c in captures if c.field not in NAME_FIELDS] + resolved
