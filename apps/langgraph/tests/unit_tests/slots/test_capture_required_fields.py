"""The capture gate for mutations (02-F, §8.4).

A mutation acts on a booking that already exists, so it needs no candidate
search — but it does need to know WHICH booking, and both modify and cancel
mapped to an empty required set.  "Cancel my booking" therefore passed the gate
with no target and the dialog never asked for one.
"""

from __future__ import annotations

import pytest

from cordia_agent.slots.capture import (
    REQUIRED_CANDIDATE_FIELDS,
    required_candidate_fields_present,
)
from cordia_agent.state import (
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
)


def _draft(operation: OperationType, **request_fields) -> BookingDraft:
    return BookingDraft(
        tenant_id="tenant_req",
        session_id="sess_req",
        schema_version="1.0.0",
        tenant_policy_version="v1",
        request=BookingRequest(operation=operation, **request_fields),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


@pytest.mark.unit
@pytest.mark.parametrize(
    "operation",
    [OperationType.modify, OperationType.cancel, OperationType.reschedule],
)
def test_every_mutation_requires_a_target(operation: OperationType) -> None:
    assert "target_booking_id" in REQUIRED_CANDIDATE_FIELDS[operation]


@pytest.mark.unit
def test_cancel_without_a_target_fails_the_gate() -> None:
    assert not required_candidate_fields_present(_draft(OperationType.cancel))


@pytest.mark.unit
def test_cancel_with_a_target_passes_the_gate() -> None:
    assert required_candidate_fields_present(
        _draft(OperationType.cancel, target_booking_id="bk_1")
    )


@pytest.mark.unit
def test_create_still_needs_no_target() -> None:
    assert "target_booking_id" not in REQUIRED_CANDIDATE_FIELDS[OperationType.create]
