"""Unit tests for answering an informational question from the published catalog.

The property under test is what the agent is willing to SAY. A booking agent that
invents a dish is worse than one that admits it cannot read the menu, so the
failure paths get as much attention here as the happy one (§21.2).
"""

from __future__ import annotations

import json

import httpx
import pytest

from cordia_agent.nodes.answer_inquiry import (
    DIETARY_REPLY,
    GENERAL_INQUIRY_REPLY,
    HOURS_UNAVAILABLE_REPLY,
    MAX_SPOKEN_ITEMS,
    MENU_UNAVAILABLE_REPLY,
    answer_inquiry,
    category_for,
    is_hours_question,
    is_menu_question,
    open_summary,
    render_price,
    route_after_identify_intent,
    spoken_time,
)
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.state import (
    AgentState,
    BookingDraft,
    BookingRequest,
    Channel,
    DialogIntent,
    OperationType,
    SourceInfo,
)
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client


_TENANT_CONTEXT = TenantContext(
    tenant_id="tenant_1",
    channel=Channel.voice,
    channel_endpoint_id="ep_1",
    configuration_version="v1",
    correlation_id="corr_1",
    session_id="sess_1",
    allowed_agent_policy_ids=["p"],
    allowed_tool_policy_ids=["p"],
    issued_at="2026-07-25T00:00:00Z",
    expires_at="2026-07-25T01:00:00Z",
)

#: Shaped exactly as CatalogController.Search emits it — a bare JSON ARRAY whose
#: items carry `category` and carry NO `catalog_version`.
SEEDED_MENU: list[dict] = [
    {
        "item_id": "itm_oysters",
        "name": "Sydney Rock Oysters",
        "description": "Half dozen, finger lime and eschalot mignonette.",
        "price_minor": 2800,
        "currency": "AUD",
        "category": "entree",
        "duration_minutes": None,
    },
    {
        "item_id": "itm_lamb",
        "name": "Slow-Roast Lamb Shoulder",
        "description": "For two.",
        "price_minor": 8800,
        "currency": "AUD",
        "category": "main",
        "duration_minutes": None,
    },
    {
        "item_id": "itm_tart",
        "name": "Burnt Honey Tart",
        "description": "Creme fraiche, poached quince.",
        "price_minor": 1850,
        "currency": "AUD",
        "category": "dessert",
        "duration_minutes": None,
    },
]


@pytest.fixture(autouse=True)
def _reset() -> None:
    yield
    reset_tool_client()


def install_client(
    *, body: object = SEEDED_MENU, status: int = 200
) -> list[dict[str, str]]:
    """Install a ToolClient serving *body*, recording every query it saw."""
    seen: list[dict[str, str]] = []

    def handle(request: httpx.Request) -> httpx.Response:
        seen.append(dict(request.url.params))
        return httpx.Response(status, content=json.dumps(body).encode())

    set_tool_client(
        ToolClient(
            httpx.AsyncClient(
                transport=httpx.MockTransport(handle), base_url="http://test"
            ),
            tenant_context=_TENANT_CONTEXT,
        )
    )
    return seen


def _state(text: str, *, draft: BookingDraft | None = None, **kwargs) -> AgentState:
    defaults: dict = {
        "tenant_id": "tenant_1",
        "session_id": "sess_1",
        "transcript_text": text,
        "transcript_is_final": True,
        "current_turn_id": "turn_001",
        "dialog_intent": DialogIntent.inquire,
        "draft": draft,
    }
    defaults.update(kwargs)
    return AgentState(**defaults)


def _draft() -> BookingDraft:
    return BookingDraft(
        tenant_id="tenant_1",
        session_id="sess_1",
        schema_version="1.0",
        tenant_policy_version="v1",
        request=BookingRequest(operation=OperationType.create),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


# ---------------------------------------------------------------------------
# The two silent bugs this feature sat behind
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_a_json_array_response_is_not_silently_empty() -> None:
    """ToolResultEnvelope.data is `dict | None`, so an array body used to fall
    through to None — a full menu was indistinguishable from an empty one."""
    install_client()

    result = await answer_inquiry(_state("what's on the menu?"))

    assert "Sydney Rock Oysters" in result["pending_inquiry_answer"]


@pytest.mark.unit
async def test_the_real_api_item_shape_parses() -> None:
    """The API sends `category` and omits `catalog_version`; the §4.3 contract
    required the latter and forbade the former, so every live hit was a
    validation error."""
    install_client()

    result = await answer_inquiry(_state("what's on the menu?"))

    assert result["pending_inquiry_answer"] != MENU_UNAVAILABLE_REPLY


# ---------------------------------------------------------------------------
# Never invent, never omit (§21.2)
# ---------------------------------------------------------------------------


@pytest.mark.unit
async def test_an_unreachable_catalog_is_not_reported_as_an_empty_menu() -> None:
    """ "We have nothing" and "I can't see the menu" are different claims, and only
    one of them is true when the lookup fails."""
    install_client(status=503, body={"status": "unavailable"})

    result = await answer_inquiry(_state("what's on the menu?"))

    assert result["pending_inquiry_answer"] == MENU_UNAVAILABLE_REPLY


@pytest.mark.unit
async def test_a_genuinely_empty_result_says_so() -> None:
    install_client(body=[])

    answer = (await answer_inquiry(_state("do you have any dessert?")))[
        "pending_inquiry_answer"
    ]

    assert "can't see anything" in answer


@pytest.mark.unit
async def test_every_dish_named_came_from_a_row() -> None:
    install_client(body=[SEEDED_MENU[0]])

    answer = (await answer_inquiry(_state("what's on the menu?")))[
        "pending_inquiry_answer"
    ]

    for other in ("Lamb", "Tart", "Gnocchi"):
        assert other not in answer


# ---------------------------------------------------------------------------
# What it sounds like
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    "price,currency,expected",
    [
        (2800, "AUD", "28 dollars"),
        (1850, "AUD", "18.50 dollars"),
        (1405, "AUD", "14.05 dollars"),
        (2800, "usd", "28 USD"),
    ],
)
def test_price_is_sayable(price: int, currency: str, expected: str) -> None:
    """A TTS voice reads "$28.00" as "dollar sign twenty eight point zero zero"."""
    assert render_price(price, currency) == expected


@pytest.mark.unit
async def test_a_long_menu_is_truncated_with_an_offer() -> None:
    """Past about four items a spoken list stops being useful."""
    install_client(body=SEEDED_MENU * 4)

    answer = (await answer_inquiry(_state("what's on the menu?")))[
        "pending_inquiry_answer"
    ]

    assert f"There's {12 - MAX_SPOKEN_ITEMS} more" in answer


@pytest.mark.unit
async def test_a_single_item_reads_as_a_sentence_not_a_list() -> None:
    install_client(body=[SEEDED_MENU[0]])

    answer = (await answer_inquiry(_state("what's on the menu?")))[
        "pending_inquiry_answer"
    ]

    assert "and" not in answer.replace("Sydney Rock Oysters", "")


# ---------------------------------------------------------------------------
# Category routing
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    "text,expected",
    [
        ("what's for dessert?", "dessert"),
        ("any mains?", "main"),
        ("what's the tasting menu?", "set_menu"),
        ("what's on the menu?", None),
    ],
)
def test_category_is_read_longest_phrase_first(text: str, expected: str | None) -> None:
    """ "tasting menu" must beat the bare word "menu"."""
    assert category_for(text) == expected


@pytest.mark.unit
async def test_a_category_question_filters_server_side() -> None:
    seen = install_client()

    await answer_inquiry(_state("what have you got for dessert?"))

    assert seen[-1]["category"] == "dessert"


# ---------------------------------------------------------------------------
# Routing — the dead-end, and the booking flow it must not break
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    "text,expected",
    [
        ("what's on the menu?", True),
        ("what do you serve?", True),
        ("do you have anything vegetarian?", True),
        ("what about seven?", False),
        ("is that confirmed?", False),
    ],
)
def test_menu_detection_is_narrow(text: str, expected: bool) -> None:
    """A false positive ENDS the turn on a booking question, so misses are the
    cheaper error."""
    assert is_menu_question(text) is expected


@pytest.mark.unit
def test_an_inquire_with_no_draft_reaches_an_answer() -> None:
    """The dead-end: this used to fall through capture_fields -> clarify -> {} and
    the caller heard "Sorry, could you say that again?" forever."""
    assert (
        route_after_identify_intent(_state("what are your hours?")) == "answer_inquiry"
    )


@pytest.mark.unit
def test_a_booking_intent_still_goes_to_capture() -> None:
    state = _state("book a table for four", dialog_intent=DialogIntent.create)

    assert route_after_identify_intent(state) == "capture_fields"


@pytest.mark.unit
def test_an_unrecognised_utterance_mid_booking_does_not_end_the_turn() -> None:
    """inquire is the classifier's fallback, so mid-booking utterances land on it
    constantly. Ending the turn there would strand the draft."""
    state = _state("what about seven?", draft=_draft())

    assert route_after_identify_intent(state) == "capture_fields"


@pytest.mark.unit
def test_a_menu_question_mid_booking_is_still_answered() -> None:
    state = _state("what's on the menu?", draft=_draft())

    assert route_after_identify_intent(state) == "answer_inquiry"


@pytest.mark.unit
async def test_a_non_food_question_gets_an_honest_redirect_not_dead_air() -> None:
    install_client()

    result = await answer_inquiry(_state("where are you located?"))

    assert result["pending_inquiry_answer"] == GENERAL_INQUIRY_REPLY


@pytest.mark.unit
async def test_answering_never_writes_the_draft() -> None:
    """Inv-1: this node captures nothing."""
    install_client()

    result = await answer_inquiry(_state("what's on the menu?", draft=_draft()))

    assert "draft" not in result


# ---------------------------------------------------------------------------
# Dietary and allergen questions (§21.2)
# ---------------------------------------------------------------------------


@pytest.mark.unit
@pytest.mark.parametrize(
    "text",
    [
        "do you have anything vegetarian?",
        "any gluten free options?",
        "I have a nut allergy",
        "is the tart dairy free?",
    ],
)
async def test_a_dietary_question_is_never_answered_from_a_keyword_search(
    text: str,
) -> None:
    """The search projection carries no allergen_info, so a keyword match finding
    nothing would report an absence that is false — the seeded venue genuinely
    has vegetarian dishes."""
    seen = install_client()

    result = await answer_inquiry(_state(text))

    assert result["pending_inquiry_answer"] == DIETARY_REPLY
    assert seen == [], "must not ask the catalog a question it cannot answer"


@pytest.mark.unit
async def test_the_dietary_reply_claims_nothing_about_the_kitchen() -> None:
    """It must not say "we have none" OR "yes we can" — it knows neither."""
    install_client()

    answer = (await answer_inquiry(_state("I have a nut allergy")))[
        "pending_inquiry_answer"
    ]

    for claim in ("we have", "we don't", "we can", "no ", "none"):
        assert claim not in answer.lower()


@pytest.mark.unit
async def test_the_plain_menu_question_lists_the_menu() -> None:
    """The headline case, and it regressed live: sending the caller's SENTENCE as
    the keyword query matched no dish, so "what's on the menu?" answered "I
    couldn't find anything matching that" against a fully-stocked catalog."""
    seen = install_client()

    answer = (await answer_inquiry(_state("what's on the menu?")))[
        "pending_inquiry_answer"
    ]

    assert seen[-1]["query"] == ""
    assert "Sydney Rock Oysters" in answer
    assert "can't see anything" not in answer


@pytest.mark.unit
async def test_the_transcript_is_never_used_as_a_keyword() -> None:
    """No dish is named after a question."""
    seen = install_client()

    await answer_inquiry(_state("what have you got for dessert?"))

    assert seen[-1]["query"] == ""
    assert seen[-1]["category"] == "dessert"


# ---------------------------------------------------------------------------
# "What time do you close tonight?"
#
# The one question a venue is rung about as often as the menu, and the agent
# answered it with a generic redirect because nothing carried the hours.
# ---------------------------------------------------------------------------

#: The seeded dev venue: closed Mondays, 08:00 to 22:00 otherwise.
SEEDED_HOURS: list[dict] = [
    {"d": "Monday", "open": "08:00", "close": "22:00", "on": False},
    {"d": "Tuesday", "open": "08:00", "close": "22:00", "on": True},
    {"d": "Wednesday", "open": "08:00", "close": "22:00", "on": True},
    {"d": "Thursday", "open": "08:00", "close": "22:00", "on": True},
    {"d": "Friday", "open": "08:00", "close": "22:00", "on": True},
    {"d": "Saturday", "open": "08:00", "close": "22:00", "on": True},
    {"d": "Sunday", "open": "08:00", "close": "22:00", "on": True},
]


def _hours_state(text: str, **kwargs) -> AgentState:
    return _state(
        text,
        tenant_hours=kwargs.pop("tenant_hours", SEEDED_HOURS),
        tenant_timezone=kwargs.pop("tenant_timezone", "Australia/Melbourne"),
        **kwargs,
    )


@pytest.mark.unit
@pytest.mark.parametrize(
    "text",
    [
        "what time do you close tonight?",
        "when do you open?",
        "what are your opening hours?",
        "are you open on Sunday?",
        "how late are you open?",
        "until when are you serving?",
    ],
)
def test_hours_questions_are_recognised(text: str) -> None:
    assert is_hours_question(text)


@pytest.mark.unit
@pytest.mark.parametrize(
    "text",
    ["seven o'clock", "there'll be six of us", "what's on the menu?"],
)
def test_booking_talk_is_not_an_hours_question(text: str) -> None:
    """A false positive ENDS the turn and strands the draft, so the guard is narrow."""
    assert not is_hours_question(text)


@pytest.mark.unit
async def test_closing_time_is_answered_from_the_venues_own_hours() -> None:
    result = await answer_inquiry(_hours_state("what time do you close tonight?"))

    assert "10pm" in result["pending_inquiry_answer"]


@pytest.mark.unit
async def test_a_named_closed_day_says_so_and_offers_the_rest_of_the_week() -> None:
    result = await answer_inquiry(_hours_state("are you open on Monday?"))

    answer = result["pending_inquiry_answer"]
    assert "closed Mondays" in answer
    assert "Tuesday to Sunday, 8am till 10pm" in answer


@pytest.mark.unit
async def test_a_named_open_day_reads_that_days_hours() -> None:
    result = await answer_inquiry(_hours_state("are you open on Sunday?"))

    assert result["pending_inquiry_answer"] == "On Sundays we're open 8am till 10pm."


@pytest.mark.unit
async def test_unknown_hours_are_admitted_rather_than_guessed() -> None:
    """A caller told the wrong closing time drives to a locked door."""
    result = await answer_inquiry(
        _hours_state("what time do you close?", tenant_hours=None)
    )

    assert result["pending_inquiry_answer"] == HOURS_UNAVAILABLE_REPLY


@pytest.mark.unit
async def test_without_a_timezone_the_week_is_read_back_rather_than_a_guessed_day() -> (
    None
):
    """No zone means no "today", and a guessed day is a wrong answer said confidently."""
    result = await answer_inquiry(
        _hours_state("what time do you close?", tenant_timezone=None)
    )

    assert (
        result["pending_inquiry_answer"]
        == "We're open Tuesday to Sunday, 8am till 10pm."
    )


@pytest.mark.unit
async def test_an_hours_question_beats_the_menu_lookup() -> None:
    """ "What time do you stop serving?" carries `serving`; read as a menu question
    it answers a question about the clock with a list of dishes."""
    install_client()

    result = await answer_inquiry(_hours_state("what time do you stop serving?"))

    assert "Oysters" not in result["pending_inquiry_answer"]


@pytest.mark.unit
def test_an_hours_question_mid_booking_still_reaches_the_answer() -> None:
    state = _hours_state("what time do you close tonight?", draft=_draft())

    assert route_after_identify_intent(state) == "answer_inquiry"


@pytest.mark.unit
@pytest.mark.parametrize(
    ("stored", "spoken"),
    [("22:00", "10pm"), ("08:00", "8am"), ("08:30", "8:30am"), ("12:00", "12pm")],
)
def test_times_are_said_the_way_a_person_says_them(stored: str, spoken: str) -> None:
    """A TTS voice reading "22:00" says "twenty two hundred"."""
    assert spoken_time(stored) == spoken


@pytest.mark.unit
def test_hours_that_differ_by_day_are_not_flattened_into_one_claim() -> None:
    """Quoting Tuesday's hours for a short Sunday states them wrong to a caller."""
    varied = [dict(d) for d in SEEDED_HOURS]
    varied[6]["close"] = "16:00"

    assert open_summary(varied) == "We're open Tuesday to Sunday."
