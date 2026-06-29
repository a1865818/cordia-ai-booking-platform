"""Unit tests for the §6.4 latency-band progress filler."""

import pytest

from cordia_agent.state import StreamFrameType
from cordia_agent.streaming import ProgressFiller, TurnManager
from cordia_agent.streaming_models import ToolProgressFrame


ENVELOPE_FIELDS = {
    "tenant_id": "tenant_1",
    "session_id": "session_1",
    "turn_id": "assistant_turn_1",
    "correlation_id": "correlation_1",
    "causation_id": "causation_1",
    "ts": "2026-06-18T00:00:00+00:00",
}


def _progress(
    elapsed_ms: int,
    *,
    tool_name: str = "check_candidate_availability",
    phase: str = "checking",
    turn_id: str = "assistant_turn_1",
    seq: int = 0,
) -> ToolProgressFrame:
    return ToolProgressFrame(
        type=StreamFrameType.TOOL_PROGRESS,
        **{**ENVELOPE_FIELDS, "turn_id": turn_id},
        seq=seq,
        tool_name=tool_name,
        phase=phase,
        elapsed_ms=elapsed_ms,
    )


@pytest.mark.unit
@pytest.mark.parametrize(
    ("elapsed_ms", "expected"),
    [
        (0, None),
        (299, None),
        (300, "just a moment"),
        (999, "just a moment"),
        (1000, "checking availability"),
        (4999, "checking availability"),
        (5000, "I'll follow up shortly"),
        (12000, "I'll follow up shortly"),
    ],
)
def test_fill_maps_elapsed_ms_to_band_phrase(elapsed_ms, expected) -> None:
    filler = ProgressFiller(turn_manager=TurnManager())
    assert filler.fill(_progress(elapsed_ms)) == expected


@pytest.mark.unit
def test_repeated_tool_call_announced_only_once() -> None:
    filler = ProgressFiller(turn_manager=TurnManager())
    frame = _progress(1500)
    assert filler.fill(frame) == "checking availability"
    assert filler.fill(frame) is None  # same (turn_id, tool_name)


@pytest.mark.unit
def test_sub_threshold_frame_does_not_burn_the_announcement() -> None:
    # A <300ms blip must NOT consume the single announcement for the tool call.
    filler = ProgressFiller(turn_manager=TurnManager())
    assert filler.fill(_progress(100)) is None
    assert filler.fill(_progress(1500)) == "checking availability"
    assert filler.fill(_progress(2000)) is None


@pytest.mark.unit
def test_distinct_tools_each_get_their_own_announcement() -> None:
    filler = ProgressFiller(turn_manager=TurnManager())
    assert filler.fill(_progress(1500, tool_name="search_vendor_catalog")) is not None
    assert filler.fill(_progress(1500, tool_name="check_verification")) is not None


@pytest.mark.unit
def test_no_band_phrase_implies_success() -> None:
    # Inv-16: never imply the booking succeeded while a tool is still pending.
    filler = ProgressFiller(turn_manager=TurnManager())
    forbidden = (
        "confirmed",
        "booked",
        "you're all set",
        "great news",
        "done",
        "success",
    )
    phrases = [
        filler.fill(_progress(ms, tool_name=f"tool_{ms}")) for ms in (300, 1000, 5000)
    ]
    for phrase in phrases:
        assert phrase is not None
        assert not any(token in phrase.lower() for token in forbidden)
