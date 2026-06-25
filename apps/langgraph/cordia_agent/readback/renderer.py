"""Pure deterministic read-back renderer (spec 02 §8.6 / §8.8, Inv-16).

This module is PURE: no LLM, no I/O.  Given the same draft+mode it always
produces byte-identical text and read_back_version (sha256 content hash).
"""

from __future__ import annotations

import hashlib
import json

from pydantic import BaseModel

from cordia_agent.contracts.operation_status import NormalizedOperationStatus
from cordia_agent.state import BookingDraft, TriState
from cordia_agent.tools.status_codes import AvailabilityMode


class ReadBackResult(BaseModel):
    """Result of a deterministic read-back render."""

    model_config = {"extra": "forbid"}

    text: str
    read_back_version: str


def _field_str(value: object) -> str:
    """Render a BookingRequest field value for human-readable output.

    TriState sentinels and None both produce explicit "not specified".
    """
    if value is None or isinstance(value, TriState):
        return "not specified"
    return str(value)


def _mode_line(mode: AvailabilityMode) -> str:
    """§6.5 availability mode wording appended at the end of the read-back."""
    if mode is AvailabilityMode.INFORMATIONAL:
        return "That time appears available."
    if mode is AvailabilityMode.LIVE:
        return "That time is available right now, subject to final confirmation."
    if mode is AvailabilityMode.HELD:
        return "I can hold that time while we finish."
    # CONFIRMED is mapped for completeness; read_back never passes it in practice
    # (confirmed wording is post-booking only — Inv-16).
    return "Your booking is confirmed."


def render_read_back(draft: BookingDraft, mode: AvailabilityMode) -> ReadBackResult:
    """Render a deterministic human-readable summary of the booking draft.

    Always produces the same ``text`` and ``read_back_version`` for the same
    ``draft`` + ``mode`` pair (Inv-16, §8.6).

    Args:
        draft: The in-dialog booking draft.
        mode: Current availability mode (controls the §6.5 wording line).

    Returns:
        A ``ReadBackResult`` with ``text`` and ``read_back_version`` (sha256
        content hash over material terms + mode.value).
    """
    req = draft.request

    # Extract material terms (the set that defines a unique booking)
    operation = req.operation.value
    service_id = _field_str(req.service_id)
    location_id = _field_str(req.location_id)
    starts_at = _field_str(req.starts_at)
    party_size = _field_str(req.party_size)
    duration_minutes = _field_str(req.duration_minutes)

    # Fixed template (NOT model prose) — deterministic
    lines = [
        f"Operation: {operation}",
        f"Service: {service_id}",
        f"Location: {location_id}",
        f"Date/time: {starts_at}",
        f"Party size: {party_size}",
        f"Duration: {duration_minutes}",
        _mode_line(mode),
    ]
    text = "\n".join(lines)

    # Content hash over canonical JSON of material terms + mode (Inv-16, §8.6)
    canonical: dict[str, str] = {
        "operation": operation,
        "service_id": service_id,
        "location_id": location_id,
        "starts_at": starts_at,
        "party_size": party_size,
        "duration_minutes": duration_minutes,
        "mode": mode.value,
    }
    canonical_json = json.dumps(canonical, sort_keys=True, separators=(",", ":"))
    read_back_version = hashlib.sha256(canonical_json.encode()).hexdigest()

    return ReadBackResult(text=text, read_back_version=read_back_version)


def status_wording(status: NormalizedOperationStatus) -> str:
    """Map a NormalizedOperationStatus to safe customer-facing wording (Inv-16).

    ONLY ``booked`` yields "confirmed/booked" language.  All other statuses
    produce submitted/processing-style wording and must NEVER include the word
    "confirmed" (Inv-16).
    """
    if status is NormalizedOperationStatus.BOOKED:
        return "Your booking is confirmed and booked."
    if status is NormalizedOperationStatus.ACCEPTED:
        return "Your request has been submitted and is being processed."
    if status is NormalizedOperationStatus.IN_PROGRESS:
        return "Your request is currently being processed."
    if status is NormalizedOperationStatus.AWAITING_USER_ACTION:
        return "Your request is awaiting further action from you."
    if status is NormalizedOperationStatus.ALTERNATIVES_REQUIRED:
        return "Your request requires alternative options to be selected."
    if status is NormalizedOperationStatus.WAITLISTED:
        return "You have been added to the waitlist."
    if status is NormalizedOperationStatus.REJECTED:
        return "Your request was not able to be fulfilled."
    if status is NormalizedOperationStatus.EXPIRED:
        return "Your request has expired."
    # FAILED
    return "There was an issue processing your request. Please try again."
