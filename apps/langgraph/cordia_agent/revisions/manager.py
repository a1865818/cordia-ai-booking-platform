"""Revision manager — pure functions for IntentRevision lifecycle (spec 02 §8.7 / Inv-10).

All functions are pure and deterministic.  No LLM, no I/O.

NOTE: ``canonical_payload_hash`` is a deterministic stand-in.  Ticket 04 owns
the real algorithm (§10.2); this implementation is intentionally compatible with
the frozen 04 contract (sha256 over canonical JSON) so tests can pin hashes.
"""

from __future__ import annotations

import hashlib
import json
from typing import TYPE_CHECKING

from cordia_agent.models.intent import (  # noqa: F401
    IntentRevision,
    confirmation_binds_revision,
)


if TYPE_CHECKING:
    from pydantic import AwareDatetime

    from cordia_agent.state import BookingRequest


def canonical_payload_hash(payload: BookingRequest) -> str:
    """Deterministic sha256 content hash of a BookingRequest.

    Algorithm: ``sha256(json.dumps(payload.model_dump(mode="json"), sort_keys=True,
    separators=(",",":")))``

    NOTE (stub): Ticket 04 owns the canonical algorithm (§10.2).  This is a
    deterministic stand-in that produces stable, reproducible hashes so the
    §8.8 binding invariant can be tested end-to-end before 04 lands.
    """
    serialized = json.dumps(
        payload.model_dump(mode="json"), sort_keys=True, separators=(",", ":")
    )
    return hashlib.sha256(serialized.encode()).hexdigest()


def build_revision(
    *,
    intent_id: str,
    revision: int,
    canonical_payload: BookingRequest,
    schema_version: str,
    tenant_policy_version: str,
    source_session_id: str,
    created_at: AwareDatetime,
    prior_revision: int | None = None,
    supersession_reason: str | None = None,
) -> IntentRevision:
    """Construct an IntentRevision with a freshly computed payload_hash.

    Args:
        intent_id: Stable intent identity across revisions.
        revision: Monotonic revision number for this intent.
        canonical_payload: The BookingRequest to hash and embed.
        schema_version: Draft schema version.
        tenant_policy_version: Tenant configuration/policy version.
        source_session_id: Dialog session that produced this revision.
        created_at: Revision creation time (offset-aware).  Pass explicitly —
            do NOT call ``datetime.now()`` here to preserve test determinism.
        prior_revision: Superseded revision number, if any.
        supersession_reason: Why the prior revision was superseded (§8.7).

    Returns:
        An immutable :class:`IntentRevision`.
    """
    return IntentRevision(
        intent_id=intent_id,
        revision=revision,
        canonical_payload=canonical_payload,
        schema_version=schema_version,
        tenant_policy_version=tenant_policy_version,
        payload_hash=canonical_payload_hash(canonical_payload),
        created_at=created_at,
        source_session_id=source_session_id,
        prior_revision=prior_revision,
        supersession_reason=supersession_reason,
    )


# Material fields that define the identity of a booking (Inv-10).
_MATERIAL_FIELDS: tuple[str, ...] = (
    "operation",
    "service_id",
    "location_id",
    "starts_at",
    "party_size",
    "duration_minutes",
    "target_booking_id",
)


def is_material_change(old: BookingRequest, new: BookingRequest) -> bool:
    """Return True if any material booking field changed between two requests.

    Material fields: operation, service_id, location_id, starts_at, party_size,
    duration_minutes, target_booking_id.  Non-material fields such as
    ``special_requests`` or ``target_booking_version`` are intentionally ignored
    (a change in ``special_requests`` alone does NOT trigger a new revision).
    """
    return any(getattr(old, field) != getattr(new, field) for field in _MATERIAL_FIELDS)


def supersede_revision(
    current: IntentRevision,
    new_payload: BookingRequest,
    *,
    reason: str,
    created_at: AwareDatetime,
) -> IntentRevision:
    """Return a NEW IntentRevision that supersedes ``current`` (Inv-10).

    The returned revision has:
    - ``revision = current.revision + 1``
    - ``prior_revision = current.revision``
    - ``supersession_reason = reason``
    - freshly computed ``payload_hash`` for ``new_payload``

    ``current`` is NOT mutated (immutable contract, Inv-10).

    Args:
        current: The existing IntentRevision being superseded.
        new_payload: The updated BookingRequest.
        reason: Human-readable reason for the supersession (§8.7).
        created_at: Creation timestamp for the new revision.

    Returns:
        A new, immutable :class:`IntentRevision`.
    """
    return build_revision(
        intent_id=current.intent_id,
        revision=current.revision + 1,
        canonical_payload=new_payload,
        schema_version=current.schema_version,
        tenant_policy_version=current.tenant_policy_version,
        source_session_id=current.source_session_id,
        created_at=created_at,
        prior_revision=current.revision,
        supersession_reason=reason,
    )
