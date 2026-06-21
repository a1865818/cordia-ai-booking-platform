"""Operation status projection crossing the service boundary (spec 06 D2)."""

from __future__ import annotations

from enum import Enum


class NormalizedOperationStatus(str, Enum):
    """Customer/agent-facing operation status from get_operation_status (spec 06)."""

    ACCEPTED = "accepted"
    IN_PROGRESS = "in_progress"
    AWAITING_USER_ACTION = "awaiting_user_action"
    ALTERNATIVES_REQUIRED = "alternatives_required"
    BOOKED = "booked"
    WAITLISTED = "waitlisted"
    REJECTED = "rejected"
    EXPIRED = "expired"
    FAILED = "failed"
