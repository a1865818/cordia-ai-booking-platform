"""Test-only stubs for the ``SlotExtractor`` port (02-F).

The ``FakeReadContextClient`` and ``ReadContextClient`` port have been removed as
part of ticket 02-H, which replaced the abstract read-context port with the
concrete 03 tool client (``cordia_agent.tools.client_binding``).

This module now contains ONLY:
- ``FakeSlotExtractor`` — deterministic fake for the ``SlotExtractor`` port used
  by ``capture_fields`` unit tests.
- ``make_service_id_capture`` / ``make_date_capture`` — factory helpers used
  across the ``capture_fields`` and ``check_candidates`` test suites.

DO NOT import this module from production code.  It must only be referenced
from test fixtures via ``register_slot_extractor`` / ``clear_slot_extractor``.
"""

from __future__ import annotations

from cordia_agent.slots.capture import SlotCapture, SlotExtractor
from cordia_agent.state import CaptureMethod


class FakeSlotExtractor:
    """Test-only deterministic slot extractor.

    Maps fixed text fragments to deterministic ``SlotCapture`` lists.
    Register via ``register_slot_extractor(FakeSlotExtractor())``.

    The ``captures`` constructor argument allows tests to control exactly which
    captures are returned, making the fake flexible without being non-deterministic.
    """

    def __init__(self, captures: list[SlotCapture] | None = None) -> None:
        """Initialise with a fixed capture list.

        Args:
            captures: The list of captures to return from :meth:`extract`.
                Defaults to an empty list (no captures).
        """
        self._captures: list[SlotCapture] = captures or []

    def extract(self, text: str) -> list[SlotCapture]:
        """Return the pre-configured captures, ignoring *text*.

        Args:
            text: Ignored; all responses are pre-configured at construction.

        Returns:
            The fixed list of :class:`~cordia_agent.slots.capture.SlotCapture`
            instances supplied at construction.
        """
        return list(self._captures)


# Runtime Protocol check: this class must satisfy SlotExtractor.
assert isinstance(FakeSlotExtractor(), SlotExtractor), (
    "FakeSlotExtractor does not satisfy the SlotExtractor Protocol"
)


def make_service_id_capture(
    service_id: str = "svc_001",
    capture_method: CaptureMethod = CaptureMethod.speech,
    stt_confidence: float = 0.95,
    model_confidence: float = 0.90,
) -> SlotCapture:
    """Convenience factory: build a ``service_id`` :class:`SlotCapture`.

    Args:
        service_id: The service ID value to capture.
        capture_method: How the value was captured.
        stt_confidence: STT confidence score.
        model_confidence: Model confidence score.

    Returns:
        A :class:`~cordia_agent.slots.capture.SlotCapture` for ``service_id``.
    """
    return SlotCapture(
        field="service_id",
        value=service_id,
        capture_method=capture_method,
        stt_confidence=stt_confidence,
        model_confidence=model_confidence,
        normalized_value=service_id,
    )
