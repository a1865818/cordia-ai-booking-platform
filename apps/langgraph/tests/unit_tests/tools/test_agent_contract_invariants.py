"""§27.1 agent-contract invariant tests (ticket 03-G).

Tests:
- Inv-1 structural: SubmitConfirmedIntentInput rejects a raw string in the
  confirmation field — transcript text can never become a submit without a
  structured Confirmation object. Also verifies no foreground tool exposes a
  free-text field that could bypass this gate.
- Inv-11 mutations require grant: SubmitModificationInput / SubmitCancellationInput
  require grant_id at the schema level (ValidationError without it). Behavioral:
  submit_booking_modification with no grant → verification_required envelope, zero
  transport hits.
- Inv-16 no booked/confirmed wording from accepted/pending statuses: status_wording()
  must not contain "booked" or "confirmed" for accepted / in_progress /
  awaiting_user_action; must contain those words for booked.
- Privileged-tool gate (Inv-1 boundary): untrusted catalog text labelled via the
  02-D injection harness cannot select or parameterize submit_confirmed_intent or
  request_verification.
"""

from __future__ import annotations

import inspect
import json

import httpx
import pytest
from pydantic import ValidationError

from cordia_agent.contracts.operation_status import NormalizedOperationStatus
from cordia_agent.readback.renderer import status_wording
from cordia_agent.safety.injection_harness import (
    ToolAllowlist,
    TrustLevel,
    is_tool_invocation_permitted,
    label_untrusted,
)
from cordia_agent.safety.tenant_context import TenantContext
from cordia_agent.state import Channel
from cordia_agent.tools.client import ToolClient, reset_tool_client, set_tool_client
from cordia_agent.tools.contracts import (
    SubmitCancellationInput,
    SubmitConfirmedIntentInput,
    SubmitModificationInput,
    ToolResultEnvelope,
)
from cordia_agent.tools.foreground import FOREGROUND_TOOLS
from cordia_agent.tools.manage_booking import submit_booking_modification
from tests.unit_tests.tools.fixtures.normalized_tool_responses import (
    UNTRUSTED_CATALOG_TEXT_WITH_INJECTION,
    make_accepted_status_view,
    make_awaiting_user_action_status_view,
    make_booked_status_view,
    make_in_progress_status_view,
)


# ---------------------------------------------------------------------------
# Shared helpers
# ---------------------------------------------------------------------------


def _make_tenant_context(*, allowed_tools: list[str] | None = None) -> TenantContext:
    return TenantContext(
        tenant_id="tenant_inv_test",
        channel=Channel.voice,
        channel_endpoint_id="ep_inv_001",
        configuration_version="v1",
        correlation_id="corr_inv_001",
        session_id="sess_inv_001",
        allowed_agent_policy_ids=[],
        allowed_tool_policy_ids=allowed_tools
        or ["submit_confirmed_intent", "request_verification"],
        issued_at="2026-07-25T00:00:00Z",
        expires_at="2026-07-25T01:00:00Z",
    )


def _make_json_response(status_code: int, body: dict) -> httpx.Response:
    return httpx.Response(
        status_code=status_code,
        headers={"content-type": "application/json"},
        content=json.dumps(body).encode(),
    )


# ---------------------------------------------------------------------------
# Inv-1: structural — raw string cannot become a SubmitConfirmedIntentInput
# ---------------------------------------------------------------------------


@pytest.mark.unit
class TestInv1StructuralNoStringToSubmit:
    """Inv-1: SubmitConfirmedIntentInput.confirmation must be a structured Confirmation."""

    def test_raw_string_confirmation_raises_validation_error(self) -> None:
        """Passing a raw string for confirmation raises Pydantic ValidationError."""
        with pytest.raises(ValidationError) as exc_info:
            SubmitConfirmedIntentInput(
                operation_id="op_inv1_001",
                idempotency_key="idem_inv1_001",
                intent_id="intent_001",
                intent_revision=1,
                intent_payload_hash="hash_abc",
                confirmation="yes book it",  # RAW STRING — must be rejected
            )
        errors = exc_info.value.errors()
        # The error must be on the confirmation field
        field_errors = [e for e in errors if "confirmation" in str(e.get("loc", ""))]
        assert field_errors, (
            f"Expected a validation error on 'confirmation' field, got: {errors}"
        )

    def test_raw_dict_missing_required_confirmation_fields_raises(self) -> None:
        """A dict that doesn't satisfy Confirmation's required fields also raises."""
        with pytest.raises(ValidationError):
            SubmitConfirmedIntentInput(
                operation_id="op_inv1_002",
                idempotency_key="idem_inv1_002",
                intent_id="intent_002",
                intent_revision=1,
                intent_payload_hash="hash_def",
                confirmation={"text": "please book"},  # invalid Confirmation shape
            )

    def test_no_foreground_tool_has_free_text_submit_path(self) -> None:
        """No FOREGROUND_TOOL exposes a bare string field that feeds into a submit_*.

        Specifically for submit_confirmed_intent: its args schema must require a
        structured Confirmation (not str) and must include intent_payload_hash.
        This proves no transcript text path can reach the submit without the
        structured confirmation gate.
        """
        submit_tool = next(
            (t for t in FOREGROUND_TOOLS if t.name == "submit_confirmed_intent"),
            None,
        )
        assert submit_tool is not None, (
            "submit_confirmed_intent must be in FOREGROUND_TOOLS"
        )

        # Introspect the args schema to confirm Confirmation is required
        schema = submit_tool.get_input_schema().model_json_schema()
        props = schema.get("properties", {})

        # Must have intent_payload_hash (hash-bound guard)
        assert "intent_payload_hash" in props, (
            "submit_confirmed_intent args must include intent_payload_hash"
        )

        # confirmation field must be present and reference Confirmation (not plain string)
        assert "confirmation" in props, (
            "submit_confirmed_intent args must include 'confirmation'"
        )
        confirmation_prop = props["confirmation"]

        # The confirmation property must NOT be typed as a plain string
        conf_type = confirmation_prop.get("type")
        assert conf_type != "string", (
            f"'confirmation' field must not be a plain string; got type={conf_type!r}. "
            "A raw transcript string must NEVER reach submit_confirmed_intent."
        )

        # The source code must not accept raw_text / transcript / free_text fields
        source = inspect.getsource(submit_tool.coroutine)  # type: ignore[arg-type]
        for forbidden in (
            "raw_text",
            "transcript_text",
            "free_text",
            "customer_speech",
        ):
            assert forbidden not in source, (
                f"submit_confirmed_intent source must not reference '{forbidden}'"
            )


# ---------------------------------------------------------------------------
# Inv-11: mutations require grant — structural + behavioral
# ---------------------------------------------------------------------------


@pytest.mark.unit
class TestInv11MutationsRequireGrant:
    """Inv-11: grant_id is required; absent grant → verification_required without transport hit."""

    def test_submit_modification_input_requires_grant_id(self) -> None:
        """SubmitModificationInput raises ValidationError when grant_id is omitted."""
        with pytest.raises(ValidationError) as exc_info:
            SubmitModificationInput(
                target_booking_id="booking_001",
                expected_booking_version=1,
                operation_id="op_mod_001",
                modification_idempotency_key="idem_mod_001",
                intent_revision=1,
                intent_payload_hash="hash_mod",
                confirmation={  # valid Confirmation dict
                    "confirmation_id": "conf_001",
                    "intent_id": "intent_001",
                    "intent_revision": 1,
                    "intent_payload_hash": "hash_mod",
                    "identity": "anonymous_session",
                    "confirmation_channel": "voice",
                    "confirmed_at": "2026-06-25T10:00:00+00:00",
                    "source_turn_id": "turn_001",
                    "read_back_version": "v1",
                    "verification_level": "none",
                },
                # grant_id is intentionally OMITTED
            )
        errors = exc_info.value.errors()
        grant_errors = [e for e in errors if "grant_id" in str(e.get("loc", ""))]
        assert grant_errors, f"Expected a validation error on 'grant_id', got: {errors}"

    def test_submit_cancellation_input_requires_grant_id(self) -> None:
        """SubmitCancellationInput raises ValidationError when grant_id is omitted."""
        with pytest.raises(ValidationError) as exc_info:
            SubmitCancellationInput(
                target_booking_id="booking_001",
                expected_booking_version=1,
                operation_id="op_cancel_001",
                cancellation_idempotency_key="idem_cancel_001",
                reason_code="customer_request",
                # grant_id is intentionally OMITTED
            )
        errors = exc_info.value.errors()
        grant_errors = [e for e in errors if "grant_id" in str(e.get("loc", ""))]
        assert grant_errors, f"Expected a validation error on 'grant_id', got: {errors}"

    @pytest.mark.asyncio
    async def test_submit_modification_no_grant_returns_verification_required_no_transport_hit(
        self,
    ) -> None:
        """Behavioral: submit_booking_modification with no grant → verification_required, 0 transport hits."""
        captured: list[httpx.Request] = []

        def _handler(request: httpx.Request) -> httpx.Response:
            captured.append(request)
            return _make_json_response(200, {"status": "accepted"})

        transport = httpx.MockTransport(_handler)
        http = httpx.AsyncClient(transport=transport, base_url="http://test")
        ctx = _make_tenant_context()
        client = ToolClient(http, tenant_context=ctx)
        set_tool_client(client)

        try:
            inp = SubmitModificationInput(
                target_booking_id="booking_001",
                expected_booking_version=1,
                operation_id="op_mod_002",
                modification_idempotency_key="idem_mod_002",
                intent_revision=1,
                intent_payload_hash="hash_mod_002",
                confirmation={
                    "confirmation_id": "conf_002",
                    "intent_id": "intent_002",
                    "intent_revision": 1,
                    "intent_payload_hash": "hash_mod_002",
                    "identity": "anonymous_session",
                    "confirmation_channel": "voice",
                    "confirmed_at": "2026-06-25T10:00:00+00:00",
                    "source_turn_id": "turn_002",
                    "read_back_version": "v1",
                    "verification_level": "none",
                },
                grant_id="",  # empty string — treated as absent by the gate
            )
            result = await submit_booking_modification.ainvoke(
                inp.model_dump(mode="json")
            )
            assert isinstance(result, ToolResultEnvelope)
            assert result.status == "verification_required", (
                f"Expected verification_required, got: {result.status!r}"
            )
            assert result.ok is False
            assert len(captured) == 0, (
                f"Transport must NOT be hit when grant is absent (Inv-11); "
                f"got {len(captured)} request(s)"
            )
        finally:
            reset_tool_client()


# ---------------------------------------------------------------------------
# Inv-16: no "booked"/"confirmed" wording from accepted/pending statuses
# ---------------------------------------------------------------------------


@pytest.mark.unit
class TestInv16NoBookedWordingFromPending:
    """Inv-16: status_wording must not return booked/confirmed for non-booked states."""

    @pytest.mark.parametrize(
        "status",
        [
            NormalizedOperationStatus.ACCEPTED,
            NormalizedOperationStatus.IN_PROGRESS,
            NormalizedOperationStatus.AWAITING_USER_ACTION,
        ],
    )
    def test_non_booked_statuses_do_not_contain_booked_or_confirmed(
        self, status: NormalizedOperationStatus
    ) -> None:
        """Wording for accepted/in_progress/awaiting_user_action must never say booked/confirmed."""
        wording = status_wording(status)
        lower = wording.lower()
        assert "booked" not in lower, (
            f"status_wording({status!r}) = {wording!r} must not contain 'booked'"
        )
        assert "confirmed" not in lower, (
            f"status_wording({status!r}) = {wording!r} must not contain 'confirmed'"
        )

    def test_booked_status_conveys_booked_and_confirmed(self) -> None:
        """Wording for booked MUST convey booked/confirmed (the only legitimate path)."""
        wording = status_wording(NormalizedOperationStatus.BOOKED)
        lower = wording.lower()
        # At least one of "booked" or "confirmed" must appear
        assert "booked" in lower or "confirmed" in lower, (
            f"status_wording(BOOKED) = {wording!r} must convey booked/confirmed"
        )

    def test_status_view_fixtures_have_correct_pending_wording(self) -> None:
        """Verify accepted/in_progress/awaiting_user_action fixture views carry non-booked wording."""
        for view in [
            make_accepted_status_view(),
            make_in_progress_status_view(),
            make_awaiting_user_action_status_view(),
        ]:
            derived = status_wording(view.status)
            lower = derived.lower()
            assert "booked" not in lower, (
                f"Derived wording for {view.status!r} must not contain 'booked': {derived!r}"
            )
            assert "confirmed" not in lower, (
                f"Derived wording for {view.status!r} must not contain 'confirmed': {derived!r}"
            )

    def test_booked_fixture_view_wording_contains_booked_or_confirmed(self) -> None:
        """Booked fixture view derives wording that mentions booked/confirmed."""
        view = make_booked_status_view()
        wording = status_wording(view.status)
        lower = wording.lower()
        assert "booked" in lower or "confirmed" in lower, (
            f"status_wording(BOOKED) must convey booked/confirmed: {wording!r}"
        )

    def test_accepted_submit_hint_never_contains_booked(self) -> None:
        """The accepted submission hint ('I have submitted the request'-style) must not say booked."""
        accepted_wording = status_wording(NormalizedOperationStatus.ACCEPTED)
        assert "booked" not in accepted_wording.lower(), (
            f"Accepted submit hint must not contain 'booked': {accepted_wording!r}"
        )


# ---------------------------------------------------------------------------
# Privileged-tool gate (Inv-1 boundary) — injection harness
# ---------------------------------------------------------------------------


@pytest.mark.unit
class TestPrivilegedToolGate:
    """Untrusted content cannot invoke submit_confirmed_intent or request_verification."""

    def test_untrusted_catalog_text_cannot_invoke_submit_confirmed_intent(self) -> None:
        """is_tool_invocation_permitted returns False for untrusted source."""
        source = label_untrusted(UNTRUSTED_CATALOG_TEXT_WITH_INJECTION)
        allowlist = ToolAllowlist(["submit_confirmed_intent", "request_verification"])

        result = is_tool_invocation_permitted(
            "submit_confirmed_intent",
            allowlist=allowlist,
            source=source,
        )
        assert result is False, (
            "Untrusted content must NEVER be permitted to invoke submit_confirmed_intent"
        )

    def test_untrusted_catalog_text_cannot_invoke_request_verification(self) -> None:
        """is_tool_invocation_permitted returns False for request_verification from untrusted."""
        source = label_untrusted(UNTRUSTED_CATALOG_TEXT_WITH_INJECTION)
        allowlist = ToolAllowlist(["submit_confirmed_intent", "request_verification"])

        result = is_tool_invocation_permitted(
            "request_verification",
            allowlist=allowlist,
            source=source,
        )
        assert result is False, (
            "Untrusted content must NEVER be permitted to invoke request_verification"
        )

    def test_untrusted_trust_level_directly_blocks_privileged_tools(self) -> None:
        """Passing TrustLevel.UNTRUSTED directly also blocks regardless of allowlist."""
        allowlist = ToolAllowlist(["submit_confirmed_intent", "request_verification"])

        for tool_name in ("submit_confirmed_intent", "request_verification"):
            result = is_tool_invocation_permitted(
                tool_name,
                allowlist=allowlist,
                source=TrustLevel.UNTRUSTED,
            )
            assert result is False, f"TrustLevel.UNTRUSTED must block {tool_name!r}"

    def test_system_trust_permits_privileged_tool_when_on_allowlist(self) -> None:
        """A SYSTEM-trusted source CAN invoke tools that are on the allowlist."""
        from cordia_agent.safety.injection_harness import label_system

        source = label_system("system prompt")
        allowlist = ToolAllowlist(["submit_confirmed_intent"])

        result = is_tool_invocation_permitted(
            "submit_confirmed_intent",
            allowlist=allowlist,
            source=source,
        )
        assert result is True, (
            "SYSTEM-trusted source must be permitted when tool is on allowlist"
        )

    def test_untrusted_injection_text_contains_privileged_tool_names_but_still_blocked(
        self,
    ) -> None:
        """Even when the untrusted text explicitly names the tool, it is blocked."""
        # The fixture text explicitly mentions "submit_confirmed_intent" and
        # "request_verification" — confirm they are present in the text
        assert "submit_confirmed_intent" in UNTRUSTED_CATALOG_TEXT_WITH_INJECTION
        assert "request_verification" in UNTRUSTED_CATALOG_TEXT_WITH_INJECTION

        source = label_untrusted(UNTRUSTED_CATALOG_TEXT_WITH_INJECTION)
        allowlist = ToolAllowlist(["submit_confirmed_intent", "request_verification"])

        # Both must still be blocked despite being mentioned in the text
        for tool_name in ("submit_confirmed_intent", "request_verification"):
            assert not is_tool_invocation_permitted(
                tool_name, allowlist=allowlist, source=source
            ), f"Mentioning {tool_name!r} in untrusted text must not grant permission"
