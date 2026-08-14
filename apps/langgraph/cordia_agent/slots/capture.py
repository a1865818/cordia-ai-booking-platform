"""Constrained slot-capture engine for the Cordia booking dialog (02-F, §8.4).

Enforces Inv-1: ONLY ``transcript.final`` frames may mutate the draft.
Calling :func:`capture_slots` with ``source_is_final=False`` returns the draft
unchanged — no partial transcripts ever write to the draft.

Circular-import contract: this module imports FROM ``cordia_agent.state``; the
``state`` module never imports from here.  ``AgentState`` lives in state.py
only.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Any, Protocol, runtime_checkable


if TYPE_CHECKING:
    from collections.abc import Sequence

from pydantic import BaseModel, Field

from cordia_agent.state import (
    BookingDraft,
    BookingRequest,
    CaptureMethod,
    FieldProvenance,
    OperationType,
    TriState,
)


# ---------------------------------------------------------------------------
# Capturable fields
# ---------------------------------------------------------------------------

#: Customer-volunteered fields that slot capture may write into ``BookingRequest``.
#: ``operation`` is intentionally excluded — it is set by IdentifyIntent, not by
#: slot extraction (§8.4 / §8.3).
CAPTURABLE_FIELDS: frozenset[str] = frozenset(
    {
        "service_id",
        "location_id",
        "resource_id",
        "starts_at",
        "party_size",
        "duration_minutes",
        "special_requests",
        "target_booking_id",
        "target_booking_version",
    }
)


# ---------------------------------------------------------------------------
# Required candidate-search fields (§6.4 / §8.4)
# ---------------------------------------------------------------------------

#: Minimum concrete fields required to initiate a candidate search, keyed by
#: ``OperationType``.  ``modify`` and ``cancel`` do not require a candidate
#: search (they operate on a known target), so they map to an empty set.
REQUIRED_CANDIDATE_FIELDS: dict[OperationType, frozenset[str]] = {
    OperationType.create: frozenset(
        {"service_id", "location_id", "starts_at", "party_size"}
    ),
    OperationType.reschedule: frozenset(
        {"service_id", "location_id", "starts_at", "party_size"}
    ),
    OperationType.modify: frozenset(),
    OperationType.cancel: frozenset(),
}


# ---------------------------------------------------------------------------
# Errors
# ---------------------------------------------------------------------------


class UnknownSlotFieldError(ValueError):
    """Raised when a :class:`SlotCapture` targets a field not in CAPTURABLE_FIELDS.

    Prevents free-form or injected field names from entering the typed draft.
    """


# ---------------------------------------------------------------------------
# SlotCapture model
# ---------------------------------------------------------------------------


class SlotCapture(BaseModel):
    """A single field value extracted from the transcript by a :class:`SlotExtractor`.

    ``extra="forbid"`` ensures that only declared attributes are accepted,
    preventing prompt-injection payloads from slipping extra keys through.
    """

    model_config = {"extra": "forbid"}

    field: str = Field(description="BookingRequest field name to capture.")
    value: Any = Field(description="Raw captured value (type-validated on merge).")
    capture_method: CaptureMethod = Field(description="How the value was captured.")
    stt_confidence: float | None = Field(
        default=None, description="STT/ASR confidence score (0.0-1.0)."
    )
    model_confidence: float | None = Field(
        default=None, description="Model confidence score (0.0-1.0)."
    )
    normalized_value: str | None = Field(
        default=None, description="Normalized string form of the captured value."
    )


# ---------------------------------------------------------------------------
# Core capture engine
# ---------------------------------------------------------------------------


def capture_slots(
    draft: BookingDraft,
    captures: Sequence[SlotCapture],
    *,
    source_is_final: bool,
    source_turn_id: str | None = None,
) -> BookingDraft:
    """Apply *captures* to *draft*, enforcing Inv-1 and field-allow-listing.

    **Inv-1**: if ``source_is_final`` is ``False`` (a partial/interim transcript),
    the draft is returned **unchanged** — no partial transcripts may mutate the
    draft.  The caller receives the same ``BookingDraft`` object; no copy is
    made for the no-op path.

    When ``source_is_final`` is ``True``:

    1. Each capture's ``field`` is validated against :data:`CAPTURABLE_FIELDS`
       (raises :exc:`UnknownSlotFieldError` on the first unknown field).
    2. All captured values are merged onto the existing ``BookingRequest`` via
       Pydantic re-validation (type-checks each value).
    3. A :class:`~cordia_agent.state.FieldProvenance` record is written for
       every captured field.
    4. A new ``BookingDraft`` instance is returned (the input is never mutated).

    Args:
        draft: The current in-dialog draft.
        captures: Sequence of :class:`SlotCapture` instances to apply.
        source_is_final: Must be ``True`` for captures to take effect (Inv-1).
        source_turn_id: Optional turn/message ID to record in provenance.

    Returns:
        An updated ``BookingDraft`` if ``source_is_final`` is ``True`` and
        captures are non-empty; the original ``draft`` otherwise.

    Raises:
        UnknownSlotFieldError: If any capture targets a field not in
            :data:`CAPTURABLE_FIELDS`.
    """
    # Inv-1: partial transcripts never mutate the draft.
    if not source_is_final:
        return draft

    if not captures:
        return draft

    # Validate all field names before applying any (fail-fast, atomic semantics).
    for capture in captures:
        if capture.field not in CAPTURABLE_FIELDS:
            raise UnknownSlotFieldError(
                f"Unknown slot field {capture.field!r}; allowed fields: "
                f"{sorted(CAPTURABLE_FIELDS)}"
            )

    # Build update dict for BookingRequest.
    update: dict[str, Any] = {}
    for capture in captures:
        update[capture.field] = capture.value

    # Re-validate via Pydantic so each value is type-checked.
    existing_dump = draft.request.model_dump(mode="python")
    existing_dump.update(update)
    new_request = BookingRequest.model_validate(existing_dump)

    # Record provenance for each captured field.
    new_provenance = dict(draft.field_provenance)
    for capture in captures:
        new_provenance[capture.field] = FieldProvenance(
            source_turn_id=source_turn_id,
            stt_confidence=capture.stt_confidence,
            model_confidence=capture.model_confidence,
            capture_method=capture.capture_method,
            normalized_value=capture.normalized_value,
        )

    return draft.model_copy(
        update={"request": new_request, "field_provenance": new_provenance}
    )


# ---------------------------------------------------------------------------
# Required-fields gate
# ---------------------------------------------------------------------------


def required_candidate_fields_present(draft: BookingDraft) -> bool:
    """Return ``True`` iff every required candidate-search field holds a concrete value.

    A field is considered concrete when its value is NOT ``None`` and NOT a
    :class:`~cordia_agent.state.TriState` sentinel (``absent``/``unknown``/
    ``declined`` are placeholders, not usable candidate-search inputs).

    Args:
        draft: The current booking draft.

    Returns:
        ``True`` if all required fields for ``draft.request.operation`` are
        concrete; ``False`` otherwise (including when the operation has no
        required fields — modify/cancel always return ``True``).
    """
    required = REQUIRED_CANDIDATE_FIELDS.get(draft.request.operation, frozenset())
    if not required:
        # modify/cancel: no candidate search needed; gate passes.
        return True

    request = draft.request
    for field_name in required:
        value = getattr(request, field_name, None)
        if value is None or isinstance(value, TriState):
            return False
    return True


# ---------------------------------------------------------------------------
# SlotExtractor port (model seam — mirrors 02-D verifier-port pattern)
# ---------------------------------------------------------------------------


@runtime_checkable
class SlotExtractor(Protocol):
    """Port: abstracts the model-backed slot extractor from production nodes.

    Production registers the real model-backed extractor at startup
    (:class:`cordia_agent.slots.extractor.ModelSlotExtractor`).
    Tests register a deterministic fake via :func:`register_slot_extractor`.

    ``extract`` is **async**: the production implementation issues a model call,
    and ``capture_fields`` is already an async node, so awaiting the port is the
    only bridge that neither blocks the event loop nor nests a second one.
    """

    async def extract(self, text: str) -> list[SlotCapture]:
        """Extract slot captures from *text*.

        Args:
            text: The transcript (or partial text) to extract slots from.

        Returns:
            A list of :class:`SlotCapture` instances (may be empty).
        """
        ...


_slot_extractor: SlotExtractor | None = None


def register_slot_extractor(v: SlotExtractor) -> None:
    """Register the concrete slot extractor for this process.

    Must be called once at startup (or in a test fixture) before any call to
    :func:`get_slot_extractor`.

    Args:
        v: The :class:`SlotExtractor` implementation to register.
    """
    global _slot_extractor
    _slot_extractor = v


def get_slot_extractor() -> SlotExtractor:
    """Return the registered slot extractor, or raise if none has been registered.

    Raises:
        RuntimeError: if :func:`register_slot_extractor` has not been called yet.
    """
    if _slot_extractor is None:
        raise RuntimeError("no slot extractor registered")
    return _slot_extractor


def clear_slot_extractor() -> None:
    """Remove the registered slot extractor — call in test teardown to isolate tests."""
    global _slot_extractor
    _slot_extractor = None
