"""FROZEN SPEC-03 READ-TOOL FAKE — replace when 02-H binds the concrete client.

Test-only stubs for the ``ReadContextClient`` and ``SlotExtractor`` ports
(02-F).  These are deterministic, side-effect-free fakes used in unit tests.

DO NOT import this module from production code.  It must only be referenced
from test fixtures (``register_read_client`` / ``clear_read_client`` and
``register_slot_extractor`` / ``clear_slot_extractor`` calls).

When 02-H lands the concrete HTTP-backed client, replace ``FakeReadContextClient``
with the real implementation and remove this module.
"""

from __future__ import annotations

from cordia_agent.nodes.load_context import ReadContextClient
from cordia_agent.slots.capture import SlotCapture, SlotExtractor
from cordia_agent.state import CaptureMethod, CustomerContext, CustomerRef


class FakeReadContextClient:
    """FROZEN SPEC-03 READ-TOOL FAKE — deterministic, test-only.

    Returns a fixed tenant policy dict and a ``CustomerContext`` that INCLUDES
    ``recent_booking_summaries`` — this makes the minimization test meaningful
    (the node must strip them when ``verification_grant_id`` is ``None``).

    Implements the ``ReadContextClient`` protocol without importing from
    production code.
    """

    def get_tenant_context(self, tenant_id: str) -> dict:
        """Return a fixed minimal tenant policy dict.

        Args:
            tenant_id: Ignored in this fake (single-tenant stub).

        Returns:
            A dict representing a minimized tenant policy.
        """
        return {
            "tenant_id": tenant_id,
            "policy_version": "v1-fake",
            "max_party_size": 20,
        }

    def find_customer_context(
        self, tenant_id: str, customer: CustomerRef | None
    ) -> CustomerContext:
        """Return a ``CustomerContext`` with sensitive history populated.

        The returned context INCLUDES ``recent_booking_summaries`` so that
        the ``load_context`` minimization test (§9.2) is meaningful.

        Args:
            tenant_id: Ignored in this fake.
            customer: Ignored in this fake.

        Returns:
            A ``CustomerContext`` with a non-empty ``recent_booking_summaries``.
        """
        return CustomerContext(
            customer_id="cust_fake_001",
            display_name="Test Customer",
            recent_booking_summaries=[
                "Booking #100 — Haircut, 2026-01-10",
                "Booking #101 — Colour, 2026-02-14",
            ],
        )


# Runtime Protocol check: this class must satisfy ReadContextClient.
assert isinstance(FakeReadContextClient(), ReadContextClient), (
    "FakeReadContextClient does not satisfy the ReadContextClient Protocol"
)


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
