"""Unit tests for the deterministic read-back renderer (02-I, §8.6 / Inv-16)."""

from __future__ import annotations

from datetime import UTC, datetime

import pytest

from cordia_agent.contracts.operation_status import NormalizedOperationStatus
from cordia_agent.readback.renderer import render_read_back, status_wording
from cordia_agent.state import (
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
    TriState,
)
from cordia_agent.tools.status_codes import AvailabilityMode


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _make_draft(**request_overrides: object) -> BookingDraft:
    """Build a minimal valid BookingDraft with optional request overrides."""
    req_kwargs: dict[str, object] = {"operation": OperationType.create}
    req_kwargs.update(request_overrides)
    return BookingDraft(
        tenant_id="tenant_test",
        session_id="sess_test",
        schema_version="1.0",
        tenant_policy_version="v1",
        request=BookingRequest(**req_kwargs),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


# ---------------------------------------------------------------------------
# Determinism tests
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_render_read_back_deterministic_text() -> None:
    """Same draft+mode → byte-identical text (called twice)."""
    draft = _make_draft(service_id="svc_1", location_id="loc_1", party_size=2)
    result1 = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    result2 = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    assert result1.text == result2.text


@pytest.mark.unit
def test_render_read_back_deterministic_version() -> None:
    """Same draft+mode → byte-identical read_back_version (sha256 hash, called twice)."""
    draft = _make_draft(service_id="svc_1", location_id="loc_1", party_size=2)
    result1 = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    result2 = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    assert result1.read_back_version == result2.read_back_version


@pytest.mark.unit
def test_render_read_back_different_mode_different_version() -> None:
    """Different mode → different read_back_version (mode is part of the hash)."""
    draft = _make_draft(service_id="svc_1", party_size=2)
    r_info = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    r_held = render_read_back(draft, AvailabilityMode.HELD)
    assert r_info.read_back_version != r_held.read_back_version


@pytest.mark.unit
def test_render_read_back_different_draft_different_version() -> None:
    """Different draft → different read_back_version."""
    draft_a = _make_draft(service_id="svc_1")
    draft_b = _make_draft(service_id="svc_2")
    r_a = render_read_back(draft_a, AvailabilityMode.INFORMATIONAL)
    r_b = render_read_back(draft_b, AvailabilityMode.INFORMATIONAL)
    assert r_a.read_back_version != r_b.read_back_version


# ---------------------------------------------------------------------------
# Mode wording tests (§6.5)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_mode_wording_informational() -> None:
    """INFORMATIONAL must still carry the §6.5 caveat that nothing is held."""
    draft = _make_draft()
    result = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    assert "can't hold it" in result.text
    assert "free" in result.text


@pytest.mark.unit
def test_mode_wording_live() -> None:
    """LIVE says the slot is free now, without the not-held caveat."""
    draft = _make_draft()
    result = render_read_back(draft, AvailabilityMode.LIVE)
    assert "free right now" in result.text
    assert "can't hold it" not in result.text


@pytest.mark.unit
def test_mode_wording_held() -> None:
    """HELD mode produces the §6.5 'hold that time' phrase."""
    draft = _make_draft()
    result = render_read_back(draft, AvailabilityMode.HELD)
    assert "hold" in result.text.lower()


@pytest.mark.unit
def test_mode_wording_informational_no_confirmed() -> None:
    """INFORMATIONAL read-back must NOT contain the word 'confirmed' (Inv-16)."""
    draft = _make_draft(service_id="svc_1", party_size=2)
    result = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    assert "confirmed" not in result.text.lower(), (
        f"Inv-16: 'confirmed' must not appear in INFORMATIONAL read-back;\n{result.text}"
    )


@pytest.mark.unit
def test_mode_wording_live_no_confirmed() -> None:
    """LIVE read-back must NOT contain the word 'confirmed' (Inv-16)."""
    draft = _make_draft(service_id="svc_1", party_size=2)
    result = render_read_back(draft, AvailabilityMode.LIVE)
    assert "confirmed" not in result.text.lower(), (
        f"Inv-16: 'confirmed' must not appear in LIVE read-back;\n{result.text}"
    )


@pytest.mark.unit
def test_mode_wording_held_no_confirmed() -> None:
    """HELD read-back must NOT contain the word 'confirmed' (Inv-16)."""
    draft = _make_draft(service_id="svc_1", party_size=2)
    result = render_read_back(draft, AvailabilityMode.HELD)
    assert "confirmed" not in result.text.lower(), (
        f"Inv-16: 'confirmed' must not appear in HELD read-back;\n{result.text}"
    )


# ---------------------------------------------------------------------------
# TriState / None → said nothing about, rather than read out as a blank field
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_tristate_unknown_renders_not_specified() -> None:
    """An unknown field is OMITTED, not read out. Nobody confirming a booking
    says "duration, not specified" — they just don't mention it."""
    draft = _make_draft(service_id=TriState.unknown, party_size=TriState.unknown)
    result = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    assert "not specified" not in result.text


@pytest.mark.unit
def test_none_field_renders_not_specified() -> None:
    """An absent field is omitted for the same reason as a TriState one."""
    draft = _make_draft()  # service_id defaults to None
    result = render_read_back(draft, AvailabilityMode.INFORMATIONAL)
    assert "not specified" not in result.text


# ---------------------------------------------------------------------------
# status_wording tests (Inv-16)
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    "status",
    [
        NormalizedOperationStatus.ACCEPTED,
        NormalizedOperationStatus.IN_PROGRESS,
        NormalizedOperationStatus.AWAITING_USER_ACTION,
        NormalizedOperationStatus.ALTERNATIVES_REQUIRED,
        NormalizedOperationStatus.WAITLISTED,
        NormalizedOperationStatus.REJECTED,
        NormalizedOperationStatus.EXPIRED,
        NormalizedOperationStatus.FAILED,
    ],
)
def test_status_wording_non_booked_no_confirmed(
    status: NormalizedOperationStatus,
) -> None:
    """Non-booked statuses must NOT produce 'confirmed' wording (Inv-16)."""
    wording = status_wording(status)
    assert "confirmed" not in wording.lower(), (
        f"Inv-16: status {status.value!r} must NOT produce 'confirmed' wording; got: {wording!r}"
    )


@pytest.mark.unit
def test_status_wording_booked_is_confirmed() -> None:
    """ONLY booked status yields 'confirmed' / 'booked' wording (Inv-16)."""
    wording = status_wording(NormalizedOperationStatus.BOOKED)
    assert "confirmed" in wording.lower() or "booked" in wording.lower(), (
        f"Inv-16: booked status must produce 'confirmed/booked' wording; got: {wording!r}"
    )


@pytest.mark.unit
@pytest.mark.parametrize("status", list(NormalizedOperationStatus))
def test_status_wording_all_9_statuses_return_str(
    status: NormalizedOperationStatus,
) -> None:
    """status_wording returns a non-empty string for every NormalizedOperationStatus."""
    wording = status_wording(status)
    assert isinstance(wording, str) and len(wording) > 0, (
        f"status_wording({status.value!r}) must return a non-empty string"
    )


# ---------------------------------------------------------------------------
# Resolved display names (what the caller actually hears)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_read_back_says_the_resolved_name_not_the_identifier() -> None:
    """A caller confirming "Location: fitzroy" has been read a database row."""
    draft = _make_draft(service_id="dinner", location_id="fitzroy", party_size=2)
    draft = draft.model_copy(
        update={
            "resolved_names": {
                "service_id": "Dinner sitting",
                "location_id": "Fitzroy dining room",
            }
        }
    )

    text = render_read_back(draft, AvailabilityMode.INFORMATIONAL).text

    assert "the Dinner sitting" in text
    assert "in the Fitzroy dining room" in text
    assert "fitzroy" not in text


@pytest.mark.unit
def test_read_back_falls_back_to_the_identifier_when_nothing_resolved_it() -> None:
    """The customer spoke the id outright; there is no declared name to prefer."""
    draft = _make_draft(service_id="svc_1", location_id="loc_1", party_size=2)

    text = render_read_back(draft, AvailabilityMode.INFORMATIONAL).text

    assert "svc_1" in text
    assert "loc_1" in text


@pytest.mark.unit
def test_a_missing_resource_still_reads_as_not_specified() -> None:
    """TriState and None must not fall through to a stale name."""
    draft = _make_draft(service_id=TriState.unknown, party_size=2)
    draft = draft.model_copy(update={"resolved_names": {"service_id": "Stale"}})

    text = render_read_back(draft, AvailabilityMode.INFORMATIONAL).text

    assert "not specified" not in text
    assert "Stale" not in text


@pytest.mark.unit
def test_renaming_a_resource_changes_the_read_back_version() -> None:
    """Consent is recorded against read_back_version, so it has to cover what the
    customer HEARD — not just the identifiers behind it."""
    base = _make_draft(service_id="dinner", location_id="fitzroy", party_size=2)
    old = base.model_copy(update={"resolved_names": {"location_id": "Fitzroy room"}})
    new = base.model_copy(update={"resolved_names": {"location_id": "The Fitzroy"}})

    assert (
        render_read_back(old, AvailabilityMode.INFORMATIONAL).read_back_version
        != render_read_back(new, AvailabilityMode.INFORMATIONAL).read_back_version
    )


@pytest.mark.unit
def test_resolved_names_never_reach_the_submitted_payload() -> None:
    """The name is how the booking is SAID; only the identifier is booked."""
    draft = _make_draft(service_id="dinner", location_id="fitzroy", party_size=2)
    draft = draft.model_copy(
        update={"resolved_names": {"location_id": "Fitzroy dining room"}}
    )

    canonical = draft.to_canonical().model_dump()

    assert "resolved_names" not in canonical
    assert canonical["request"]["location_id"] == "fitzroy"


# ---------------------------------------------------------------------------
# The venue's clock, not the stored instant
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_read_back_says_the_time_the_caller_asked_for() -> None:
    """The draft stores 09:00Z because that is what capacity is keyed on. Saying
    it aloud confirms nine in the morning to someone who asked for seven pm."""
    draft = _make_draft(
        service_id="dinner",
        location_id="fitzroy",
        party_size=4,
        starts_at=datetime(2026, 9, 14, 9, 0, tzinfo=UTC),
    )

    text = render_read_back(draft, AvailabilityMode.LIVE, "Australia/Melbourne").text

    assert "7pm" in text
    assert "09:00" not in text


@pytest.mark.unit
def test_the_spoken_date_is_not_an_iso_stamp() -> None:
    """A caller cannot hear an offset."""
    draft = _make_draft(party_size=2, starts_at=datetime(2026, 9, 14, 9, 0, tzinfo=UTC))

    text = render_read_back(draft, AvailabilityMode.LIVE, "Australia/Melbourne").text

    assert "Monday 14 September at 7pm" in text
    assert "+00:00" not in text


@pytest.mark.unit
def test_an_unknown_zone_falls_back_to_the_stored_value() -> None:
    """Better a clumsy timestamp than a confidently wrong hour."""
    draft = _make_draft(party_size=2, starts_at=datetime(2026, 9, 14, 9, 0, tzinfo=UTC))

    text = render_read_back(draft, AvailabilityMode.LIVE, None).text

    assert "09:00" in text


@pytest.mark.unit
def test_the_spoken_time_is_in_the_read_back_hash() -> None:
    """Consent is recorded against read_back_version, so it must cover what the
    customer HEARD — two zones render the same instant as two different times."""
    draft = _make_draft(party_size=2, starts_at=datetime(2026, 9, 14, 9, 0, tzinfo=UTC))

    melbourne = render_read_back(draft, AvailabilityMode.LIVE, "Australia/Melbourne")
    perth = render_read_back(draft, AvailabilityMode.LIVE, "Australia/Perth")

    assert melbourne.read_back_version != perth.read_back_version


# ---------------------------------------------------------------------------
# It should sound like a person, not a form
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_the_read_back_is_a_sentence_not_a_field_list() -> None:
    """A caller hears this aloud. "Operation: create / Service: Dinner sitting"
    is a schema being read out, including a field name nobody has words for."""
    draft = _make_draft(service_id="dinner", location_id="fitzroy", party_size=4)

    text = render_read_back(draft, AvailabilityMode.LIVE).text

    assert "\n" not in text
    for field_name in (
        "Operation:",
        "Service:",
        "Location:",
        "Party size:",
        "Duration:",
    ):
        assert field_name not in text


@pytest.mark.unit
def test_the_read_back_ends_by_handing_the_turn_back() -> None:
    """A read-back IS a question — the customer is being asked to approve terms.
    Stating a fact and stopping leaves a silence they have to interpret."""
    draft = _make_draft(service_id="dinner", party_size=2)

    for mode in (
        AvailabilityMode.LIVE,
        AvailabilityMode.INFORMATIONAL,
        AvailabilityMode.HELD,
    ):
        assert render_read_back(draft, mode).text.rstrip().endswith("?")


@pytest.mark.unit
def test_a_party_size_is_spoken_as_words() -> None:
    """TTS reads "4" as a digit; a person says "four people"."""
    draft = _make_draft(service_id="dinner", party_size=4)

    assert "four people" in render_read_back(draft, AvailabilityMode.LIVE).text


@pytest.mark.unit
def test_one_person_is_not_one_people() -> None:
    draft = _make_draft(service_id="dinner", party_size=1)

    assert "one person" in render_read_back(draft, AvailabilityMode.LIVE).text


@pytest.mark.unit
def test_the_venue_name_keeps_its_capitals() -> None:
    """ "the fitzroy dining room" reads as a typo out loud."""
    draft = _make_draft(service_id="dinner", location_id="fitzroy", party_size=2)
    draft = draft.model_copy(
        update={"resolved_names": {"location_id": "Fitzroy dining room"}}
    )

    assert (
        "the Fitzroy dining room" in render_read_back(draft, AvailabilityMode.LIVE).text
    )


@pytest.mark.unit
def test_an_on_the_hour_time_drops_the_minutes() -> None:
    """Nobody says "seven o'clock zero zero"."""
    draft = _make_draft(party_size=2, starts_at=datetime(2026, 9, 14, 9, 0, tzinfo=UTC))

    text = render_read_back(draft, AvailabilityMode.LIVE, "Australia/Melbourne").text

    assert "at 7pm" in text
    assert "7:00" not in text


@pytest.mark.unit
def test_the_read_back_is_still_byte_identical_for_the_same_draft() -> None:
    """Prose did not cost determinism — Inv-16 never required a field list."""
    draft = _make_draft(service_id="dinner", location_id="fitzroy", party_size=4)

    first = render_read_back(draft, AvailabilityMode.LIVE, "Australia/Melbourne")
    second = render_read_back(draft, AvailabilityMode.LIVE, "Australia/Melbourne")

    assert first.text == second.text
    assert first.read_back_version == second.read_back_version
