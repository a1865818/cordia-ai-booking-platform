"""Node: answer_inquiry — what the agent says to a question it cannot book.

``identify_intent`` has always been able to classify ``inquire``, and until this
node existed that classification went nowhere.  The unconditional
``identify_intent -> capture_fields`` edge sent an informational utterance into
the capture loop with NO draft (``inquire`` is the one intent that builds none),
``route_after_capture_fields`` saw ``draft is None`` and routed to ``clarify``,
``_pick_clarify_field`` returned ``None`` because there was no draft to inspect,
and clarify returned ``{}``.  An empty update means no customer-facing text, so
``reply_text`` fell through to its fallback and the caller heard "Sorry, could
you say that again?" — forever, because nothing in that path changes state, so
the next turn took it again.

What this node adds is narrow on purpose: the venue's published menu is real
data sitting behind ``search_vendor_catalog``, and the opening hours now ride on
``get_tenant_context``, so answering "what's on the menu?" or "what time do you
close tonight?" needs a sentence over data the agent already holds, not a new
booking capability.  Ordering is NOT here — capturing dishes into a ``PreorderDraft`` and driving the preorder
saga is a separate piece of work.

Two rules govern what it is allowed to say:

- **Never invent, never omit (§21.2).**  Every dish named comes from a catalog
  row.  When the tool fails the agent says it cannot reach the menu; it does not
  say the menu is empty, because a failed lookup and an empty kitchen are not
  the same claim.
- **Catalog text is UNTRUSTED (§8.11, spec 14).**  Item names and descriptions
  are menu data written by a vendor, not instructions.  They are interpolated
  into a fixed template and never concatenated into a prompt.
"""

from __future__ import annotations

import logging
from datetime import datetime
from itertools import pairwise
from typing import TYPE_CHECKING
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

# AgentState is imported at RUNTIME, not under TYPE_CHECKING: LangGraph calls
# get_type_hints() on every router to infer its input schema, and a
# string annotation it cannot resolve raises NameError at graph build.
from cordia_agent.nodes.identify_intent import awaiting_verification_code
from cordia_agent.state import AgentState, DialogIntent
from cordia_agent.tools.client_binding import parse_catalog_search, search_catalog_call


if TYPE_CHECKING:
    from cordia_agent.tools.contracts import CatalogItem


_log = logging.getLogger(__name__)

#: How many dishes to read out.  A spoken list stops being useful long before a
#: printed one does: past about four items the caller has forgotten the first.
MAX_SPOKEN_ITEMS: int = 4

#: Category words a caller says -> the catalog ``category`` to filter on.  The
#: seeded catalog uses these values verbatim.
CATEGORY_WORDS: dict[str, str] = {
    "entree": "entree",
    "starter": "entree",
    "appetiser": "entree",
    "appetizer": "entree",
    "main": "main",
    "mains": "main",
    "side": "side",
    "sides": "side",
    "dessert": "dessert",
    "desserts": "dessert",
    "pudding": "dessert",
    "set menu": "set_menu",
    "tasting": "set_menu",
    "tasting menu": "set_menu",
}

#: Words that make an utterance a question about FOOD rather than about the
#: booking.  Deliberately narrow: this node ends the turn, so a booking question
#: ("what about seven?") must NOT be caught here.
MENU_WORDS: tuple[str, ...] = (
    "menu",
    "eat",
    "food",
    "dish",
    "dishes",
    "serve",
    "serving",
    "cuisine",
    "vegetarian",
    "vegan",
    "gluten",
    "allergy",
    "allergies",
    "allergen",
    "dessert",
    "entree",
    "starter",
    "main course",
    "drink",
    "wine",
)

#: Dietary and allergen words.  These are NOT answerable from this endpoint: the
#: search projection (``CatalogItemSummary``) carries name, description, price,
#: currency, category and duration — no allergen_info, no dietary labels.  The
#: seeded venue genuinely HAS vegetarian dishes, marked in the ``allergen_info``
#: jsonb that only ``GET /api/catalog/items/{itemId}`` returns.
#:
#: So a keyword search for "vegetarian" matches nothing and the no-match line
#: ("I couldn't find anything matching that") would assert an absence that is
#: false.  For allergens that is not merely unhelpful — §21.2 singles out
#: never inventing or omitting allergens, and a wrong answer here reaches
#: someone who asked because it matters medically.
DIETARY_WORDS: tuple[str, ...] = (
    "vegetarian",
    "vegan",
    "gluten",
    "dairy",
    "nut",
    "nuts",
    "shellfish",
    "allergy",
    "allergies",
    "allergen",
    "intolerant",
    "intolerance",
    "coeliac",
    "celiac",
)

#: Said to a dietary or allergen question.  Claims nothing about what the kitchen
#: can do, and routes to someone who can actually check.
DIETARY_REPLY: str = (
    "I'd hate to get that wrong, so I won't guess — let me put you through to "
    "the kitchen and they'll know for certain. Or I can read you the menu in the "
    "meantime. Which would you prefer?"
)

#: Phrases that are ALONE enough to mean "when are you open".  Each is specific
#: enough that no booking utterance says it by accident.
HOURS_PHRASES: tuple[str, ...] = (
    "opening hours",
    "open on",
    "closed on",
    "what time do you",
    "how late",
    "how early",
    "until when",
    "till when",
)

#: The subject half of an hours question.
HOURS_SUBJECT_WORDS: tuple[str, ...] = (
    "open",
    "close",
    "closing",
    "closed",
    "hours",
    "shut",
)

#: The question half.  Both halves are required, because "open" on its own is a
#: word a caller uses mid-booking ("is seven still open?") and this node ENDS the
#: turn: a false positive abandons a booking, a miss merely answers late.
HOURS_QUESTION_WORDS: tuple[str, ...] = (
    "what time",
    "when",
    "are you",
    "do you",
    "what are your",
    "how late",
)

#: Weekday words a caller may name, in the order the hours list uses them.
WEEKDAYS: tuple[str, ...] = (
    "monday",
    "tuesday",
    "wednesday",
    "thursday",
    "friday",
    "saturday",
    "sunday",
)

#: Said when the hours are not on state.  An honest "I don't know" rather than a
#: guess: a caller told the wrong closing time drives to a locked door.
HOURS_UNAVAILABLE_REPLY: str = (
    "I don't have our opening times in front of me just this second, sorry. "
    "I can put you through to someone who'll know, or get you booked in."
)

#: Said when the caller asked something informational that is not about food.
#: Not an answer — an honest redirect, which is still infinitely better than the
#: dead air this node replaces.
GENERAL_INQUIRY_REPLY: str = (
    "I can get you a table booked in, or run you through the menu. What would suit?"
)

#: Said when the catalog lookup fails.  NOT "we have nothing" — see §21.2 above.
MENU_UNAVAILABLE_REPLY: str = (
    "Sorry, I can't pull the menu up just this second. I can still get you "
    "booked in, or pass you to someone who can talk you through it."
)

#: Said when the catalog is reachable and genuinely returned no match.
NO_MATCH_REPLY_TEMPLATE: str = (
    "I can't see anything like {query} on the menu, sorry. "
    "Would you like to hear what we do have?"
)


def is_dietary_question(text: str) -> bool:
    """Return ``True`` when *text* asks about dietary requirements or allergens.

    Checked BEFORE the menu lookup, because the honest answer is that this data
    is not on the endpoint — not a keyword search that finds nothing and reports
    it as an absence.
    """
    lower = text.lower()
    return any(word in lower for word in DIETARY_WORDS)


def is_hours_question(text: str) -> bool:
    """Return ``True`` when *text* asks when the venue is open.

    Checked BEFORE the menu, because "what time do you stop serving?" contains
    ``serving`` and would otherwise be answered with a list of dishes.
    """
    lower = text.lower()
    if any(phrase in lower for phrase in HOURS_PHRASES):
        return True
    return any(word in lower for word in HOURS_SUBJECT_WORDS) and any(
        word in lower for word in HOURS_QUESTION_WORDS
    )


def spoken_time(hhmm: str) -> str:
    """Render ``"22:00"`` as ``"10pm"`` and ``"08:30"`` as ``"8:30am"``.

    A TTS voice reading "22:00" says "twenty two hundred", which is not how
    anyone has ever been told when a restaurant closes.  An unparseable value is
    returned untouched: saying the stored string is odd, inventing a time is worse.
    """
    try:
        hour_str, minute_str = hhmm.split(":")
        hour, minute = int(hour_str), int(minute_str)
    except (AttributeError, ValueError):
        return hhmm
    suffix = "am" if hour < 12 else "pm"
    hour12 = hour % 12 or 12
    return f"{hour12}{suffix}" if minute == 0 else f"{hour12}:{minute:02d}{suffix}"


def _hour_of(hhmm: str) -> int | None:
    """Return the hour in ``"HH:MM"``, or ``None`` when it does not parse."""
    try:
        return int(hhmm.split(":")[0])
    except (AttributeError, ValueError):
        return None


def _venue_now(timezone: str | None) -> datetime | None:
    """Return the current time at the venue, or ``None`` when the zone is unknown."""
    if not timezone:
        return None
    try:
        return datetime.now(ZoneInfo(timezone))
    except (ZoneInfoNotFoundError, ValueError):
        _log.warning("unrecognised venue timezone %r; not guessing the day", timezone)
        return None


def _day_named(hours: list[dict], name: str) -> dict | None:
    """Return the hours row for weekday *name*, or ``None`` when it is missing."""
    for day in hours:
        if str(day.get("d", "")).lower() == name:
            return day
    return None


def open_summary(hours: list[dict]) -> str:
    """Render "We're open Tuesday to Sunday, 8am till 10pm." from *hours*.

    The times are only quoted when every open day shares them.  A venue with a
    short Sunday would otherwise have its hours stated wrong to a caller, which
    is worse than stating fewer of them.
    """
    open_days = [d for d in hours if d.get("on")]
    if not open_days:
        return "I can't see any opening times for us, sorry."

    names = [str(d.get("d", "")) for d in open_days]
    if len(open_days) == len(hours):
        phrase = "every day"
    elif len(open_days) == 1:
        phrase = f"{names[0]}s"
    else:
        indexes = [hours.index(d) for d in open_days]
        contiguous = all(b == a + 1 for a, b in pairwise(indexes))
        phrase = (
            f"{names[0]} to {names[-1]}"
            if contiguous
            else ", ".join(names[:-1]) + f", and {names[-1]}"
        )

    first = open_days[0]
    uniform = all(
        d.get("open") == first.get("open") and d.get("close") == first.get("close")
        for d in open_days
    )
    if not uniform:
        return f"We're open {phrase}."
    return (
        f"We're open {phrase}, "
        f"{spoken_time(str(first.get('open', '')))} till "
        f"{spoken_time(str(first.get('close', '')))}."
    )


def hours_answer(hours: list[dict] | None, timezone: str | None, text: str) -> str:
    """Answer "what time do you close tonight?" from the venue's own hours.

    Args:
        hours: ``state.tenant_hours`` as loaded from ``get_tenant_context``.
        timezone: ``state.tenant_timezone``, which is what makes "tonight" a day.
        text: The caller's utterance, which may name a weekday or "tomorrow".

    Returns:
        A spoken answer.  When the hours are unknown it says so: a wrong closing
        time sends someone to a locked door, and that is worse than an honest
        "let me put you through".
    """
    if not hours:
        return HOURS_UNAVAILABLE_REPLY

    lower = text.lower()
    named = next((d for d in WEEKDAYS if d in lower), None)
    label: str | None = None

    if named is not None:
        day = _day_named(hours, named)
    else:
        now = _venue_now(timezone)
        if now is None:
            # No zone means no "today", and a guessed day is a wrong answer said
            # confidently.  The whole week is still true.
            return open_summary(hours)
        label = "tomorrow" if "tomorrow" in lower else "today"
        offset = 1 if label == "tomorrow" else 0
        day = _day_named(hours, WEEKDAYS[(now.weekday() + offset) % 7])

    if day is None:
        return open_summary(hours)

    name = str(day.get("d", ""))
    opens = spoken_time(str(day.get("open", "")))
    closes = spoken_time(str(day.get("close", "")))

    if not day.get("on"):
        when = label or f"{name}s"
        return f"We're closed {when}, sorry. {open_summary(hours)}"

    if label == "today":
        # "Tonight" only when we are actually open into the evening; a bakery
        # shutting at two does not close "tonight".
        close_hour = _hour_of(str(day.get("close", "")))
        evening = close_hour is not None and close_hour >= 17
        return f"We're open till {closes} {'tonight' if evening else 'today'}."
    if label == "tomorrow":
        return f"Tomorrow we're open {opens} till {closes}."
    return f"On {name}s we're open {opens} till {closes}."


def is_menu_question(text: str) -> bool:
    """Return ``True`` when *text* is asking about food.

    Args:
        text: The transcript utterance.

    Returns:
        ``True`` if any :data:`MENU_WORDS` term appears.  A miss is safe: the
        caller gets :data:`GENERAL_INQUIRY_REPLY` or stays in the booking flow,
        both of which are recoverable.  A false POSITIVE is what costs — it ends
        the turn on a booking question — so the list stays narrow.
    """
    lower = text.lower()
    return any(word in lower for word in MENU_WORDS)


def category_for(text: str) -> str | None:
    """Return the catalog category *text* asks about, or ``None`` for the whole menu.

    Longest phrase first, so "tasting menu" beats "menu" and "main course"
    is not read as the bare word "main".
    """
    lower = text.lower()
    for word in sorted(CATEGORY_WORDS, key=len, reverse=True):
        if word in lower:
            return CATEGORY_WORDS[word]
    return None


def render_price(price_minor: int, currency: str) -> str:
    """Render a minor-unit price as something sayable.

    ``2800``/``AUD`` becomes "28 dollars"; ``2850`` becomes "28.50".  A TTS voice
    reading "$28.00" says "dollar sign twenty eight point zero zero".
    """
    major, minor = divmod(price_minor, 100)
    unit = "dollars" if currency.upper() == "AUD" else currency.upper()
    if minor == 0:
        return f"{major} {unit}"
    return f"{major}.{minor:02d} {unit}"


def render_menu_answer(items: list[CatalogItem], category: str | None) -> str:
    """Render the spoken answer for *items*.

    Args:
        items: Catalog rows, already filtered by the API.
        category: The category asked about, or ``None`` for the whole menu.

    Returns:
        A fixed-template sentence naming at most :data:`MAX_SPOKEN_ITEMS` dishes
        with their prices.  Item text is interpolated as DATA (§8.11).
    """
    spoken = items[:MAX_SPOKEN_ITEMS]
    phrases = [f"{i.name}, {render_price(i.price_minor, i.currency)}" for i in spoken]

    if len(phrases) == 1:
        listed = phrases[0]
    else:
        listed = ", ".join(phrases[:-1]) + f", and {phrases[-1]}"

    lead = "We've got" if category is None else f"For {category}, we've got"
    answer = f"{lead} {listed}."

    remaining = len(items) - len(spoken)
    if remaining > 0:
        answer += f" There's {remaining} more if you'd like to hear them."
    return answer


async def answer_inquiry(state: AgentState) -> dict:
    """Answer an informational question from the venue's own data where there is any.

    Hours come from ``state.tenant_hours``; food comes from the published catalog.

    Ends the turn either way: the caller asked something and is waiting.

    Args:
        state: Current :class:`~cordia_agent.state.AgentState`.

    Returns:
        A partial state update carrying ``pending_inquiry_answer``.  Writes no
        draft field — this node captures nothing (Inv-1).
    """
    text = state.transcript_text or ""

    # Hours first: "what time do you stop serving?" carries `serving`, and read as
    # a menu question it answers a question about the clock with a list of dishes.
    if is_hours_question(text):
        return {
            "pending_inquiry_answer": hours_answer(
                state.tenant_hours, state.tenant_timezone, text
            )
        }

    if is_dietary_question(text):
        return {"pending_inquiry_answer": DIETARY_REPLY}

    if not is_menu_question(text):
        return {"pending_inquiry_answer": GENERAL_INQUIRY_REPLY}

    category = category_for(text)
    # `query` is a keyword match over item name/description, so the caller's
    # SENTENCE is the one thing it must never receive: no dish is named "what's
    # on the menu", the search returns nothing, and the agent reports an absence
    # that is false.  An empty query lists the catalog and lets `category` do the
    # narrowing, which is the whole of what this node promises.
    #
    # Answering "do you have steak?" by name would need a dish-keyword extractor.
    # There isn't one, and guessing at the keyword by stripping question words is
    # the kind of heuristic that fails silently — the caller just hears the wrong
    # menu.  Listing is right-or-verbose; guessing is right-or-wrong.
    result = parse_catalog_search(
        await search_catalog_call(
            query="",
            category=category,
            max_results=MAX_SPOKEN_ITEMS * 3,
        )
    )

    if result is None:
        _log.info("catalog lookup failed; telling the caller so rather than guessing")
        return {"pending_inquiry_answer": MENU_UNAVAILABLE_REPLY}

    if not result.items:
        subject = f"'{category}'" if category else "that"
        return {"pending_inquiry_answer": NO_MATCH_REPLY_TEMPLATE.format(query=subject)}

    return {"pending_inquiry_answer": render_menu_answer(result.items, category)}


def route_after_identify_intent(state: AgentState) -> str:
    """Route an ``inquire`` utterance to the answer node, everything else to capture.

    The guard is deliberately conservative, because this node ENDS the turn and
    a wrong turn-end abandons a booking mid-flow:

    - A booking intent always goes to ``capture_fields``, unchanged.
    - ``inquire`` WITH a draft in progress goes to ``capture_fields`` too, unless
      it is plainly about food or about when the venue is open.  ``inquire`` is the fallback for anything the
      keyword classifier does not recognise, so mid-booking utterances like
      "what about seven?" land on it constantly; sending those here would end
      the turn and strand the draft. ``capture_fields`` re-runs the extractor,
      which is what can pull "seven" out of them.
    - ``inquire`` with NO draft has nowhere else to go.  That is the dead-end
      path, and it now reaches an answer instead of the fallback line.

    Args:
        state: Current :class:`~cordia_agent.state.AgentState`.

    Returns:
        ``"answer_inquiry"``, ``"capture_fields"`` or ``"verify_identity"``.
    """
    # A caller reading a verification code out ("four eight one five oh two")
    # classifies as nothing useful, and the slot extractor can plausibly read it
    # as a party size.  Skip both and hand the digits to the node that asked.
    if awaiting_verification_code(state):
        return "verify_identity"
    if state.dialog_intent is not DialogIntent.inquire:
        return "capture_fields"
    utterance = state.transcript_text or ""
    if (
        state.draft is not None
        and not is_menu_question(utterance)
        and not is_hours_question(utterance)
    ):
        return "capture_fields"
    return "answer_inquiry"
