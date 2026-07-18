"""Node-facing typed wrappers over the 03 foreground tools (02-H §client_binding).

Each wrapper invokes the corresponding tool via ``get_tool_client().call_tool(...)``
(by calling ``tool.ainvoke(...)`` — which is the invocation form verified by the
03-E integration tests) and exposes a companion ``parse_*`` helper that
``model_validate``s the returned ``ToolResultEnvelope.data`` into the typed
contract model.

Design:
- Wrappers are thin — NO business logic, NO branching policy, NO state mutation.
- The 03 tools are imported directly from their module (not via ``tools.__init__``)
  to satisfy the 03-E constraint that ``tools/__init__.py`` stays minimal.
- ``from __future__ import annotations`` is used throughout.
- ``parse_*`` helpers return ``None`` when the envelope is not ``ok`` or when
  ``data`` is missing/invalid, so callers can handle degraded responses gracefully.

Spec refs: 02-H §client_binding, spec 03 §4.3.
"""

from __future__ import annotations

import logging
from typing import TYPE_CHECKING

from cordia_agent.tools.contracts import (
    AcceptanceResult,
    AvailabilityRequest,
    AvailabilityResult,
    ConsentPolicyView,
    ConsentRecordedResult,
    CustomerContextView,
    HandoffRequest,
    HandoffResult,
    ModelTraceInput,
    OperationStatusView,
    PolicyValidationResult,
    RecordConsentInput,
    SubmitConfirmedIntentInput,
    TenantContextView,
    ToolResultEnvelope,
    TranscriptTurnInput,
    TranscriptTurnPersisted,
    VerificationGrantView,
)
from cordia_agent.tools.foreground import (
    append_transcript_turn,
    check_candidate_availability,
    check_verification,
    find_customer_context,
    get_operation_status,
    get_tenant_context,
    record_consent,
    record_model_trace,
    request_human_handoff,
    resolve_consent_policy,
    submit_confirmed_intent,
    validate_booking_draft,
)


if TYPE_CHECKING:
    from pydantic import BaseModel

    from cordia_agent.tools._external_stubs import BookingDraft


_log = logging.getLogger(__name__)

# Envelope-level keys that appear in the raw API body but are NOT part of any
# typed tool-output model.  These must be stripped before passing ``env.data``
# to ``model_validate`` on any ``extra="forbid"`` model.
_ENVELOPE_KEYS: frozenset[str] = frozenset(
    {
        "status",
        "ok",
        "customer_hint",
        "recoverable_fields",
        "suggested_action",
        "correlation_id",
        "schema_version",
    }
)


def _payload(data: dict, model: type[BaseModel]) -> dict:
    """Strip envelope-only keys from *data* before model_validate against *model*.

    The ``ToolClient`` stores the full response body in ``ToolResultEnvelope.data``.
    That body includes envelope keys like ``"status"`` which typed output models
    (``extra="forbid"``) reject. We strip ONLY the envelope keys that are NOT also
    declared fields of the target model — otherwise we would silently drop genuine
    payload fields that happen to share a name with an envelope key (e.g.
    ``PolicyValidationResult.recoverable_fields`` / ``.suggested_action``).

    Args:
        data: The raw ``ToolResultEnvelope.data`` dict.
        model: The target ``extra="forbid"`` model the payload will validate against.

    Returns:
        A copy of *data* with envelope-only keys (not present on *model*) removed.
    """
    strip = _ENVELOPE_KEYS - set(model.model_fields)
    return {k: v for k, v in data.items() if k not in strip}


# ---------------------------------------------------------------------------
# get_tenant_context wrapper
# ---------------------------------------------------------------------------


async def load_tenant_context() -> ToolResultEnvelope:
    """Invoke the get_tenant_context tool and return the raw envelope.

    Calls ``get_tenant_context.ainvoke({})`` — the form verified by 03-E tests.
    Does NOT set tenant context on the ToolClient; that is done by resolve_tenant.

    Returns:
        The ``ToolResultEnvelope`` from the tool (ok=True on success,
        ok=False on any transport/HTTP error with a safe hint).
    """
    return await get_tenant_context.ainvoke({})


def parse_tenant_context(env: ToolResultEnvelope) -> TenantContextView | None:
    """Parse a ``TenantContextView`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`load_tenant_context`.

    Returns:
        A validated ``TenantContextView`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None`` (degraded — caller logs and continues).
    """
    if not env.ok or env.data is None:
        _log.warning(
            "load_tenant_context returned non-ok or empty data: %s", env.status
        )
        return None
    try:
        return TenantContextView.model_validate(_payload(env.data, TenantContextView))
    except Exception:
        _log.exception("Failed to parse TenantContextView from envelope data")
        return None


# ---------------------------------------------------------------------------
# find_customer_context wrapper
# ---------------------------------------------------------------------------


async def discover_customer_context(
    *,
    phone_e164: str | None = None,
    email: str | None = None,
    caller_id: str | None = None,
    booking_reference: str | None = None,
) -> ToolResultEnvelope:
    """Invoke the find_customer_context tool and return the raw envelope.

    Passes only the non-None lookup parameters so the tool call is minimal
    (§9.2 — no over-fetching).

    Args:
        phone_e164: E.164 phone number lookup key, if available.
        email: Email lookup key, if available.
        caller_id: Caller-ID lookup key, if available.
        booking_reference: Booking-reference lookup key, if available.

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    params: dict[str, str] = {}
    if phone_e164 is not None:
        params["phone_e164"] = phone_e164
    if email is not None:
        params["email"] = email
    if caller_id is not None:
        params["caller_id"] = caller_id
    if booking_reference is not None:
        params["booking_reference"] = booking_reference
    return await find_customer_context.ainvoke(params)


def parse_customer_context(env: ToolResultEnvelope) -> CustomerContextView | None:
    """Parse a ``CustomerContextView`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`discover_customer_context`.

    Returns:
        A validated ``CustomerContextView`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None``.
    """
    if not env.ok or env.data is None:
        _log.warning(
            "discover_customer_context returned non-ok or empty data: %s", env.status
        )
        return None
    try:
        return CustomerContextView.model_validate(
            _payload(env.data, CustomerContextView)
        )
    except Exception:
        _log.exception("Failed to parse CustomerContextView from envelope data")
        return None


# ---------------------------------------------------------------------------
# validate_booking_draft wrapper
# ---------------------------------------------------------------------------


async def validate_draft(draft: BookingDraft) -> ToolResultEnvelope:
    """Invoke the validate_booking_draft tool and return the raw envelope.

    Args:
        draft: The ``BookingDraft`` to validate against tenant policy.

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    return await validate_booking_draft.ainvoke(draft.model_dump(mode="json"))


def parse_policy_validation(env: ToolResultEnvelope) -> PolicyValidationResult | None:
    """Parse a ``PolicyValidationResult`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`validate_draft`.

    Returns:
        A validated ``PolicyValidationResult`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None``.
    """
    if not env.ok or env.data is None:
        _log.warning("validate_draft returned non-ok or empty data: %s", env.status)
        return None
    try:
        return PolicyValidationResult.model_validate(
            _payload(env.data, PolicyValidationResult)
        )
    except Exception:
        _log.exception("Failed to parse PolicyValidationResult from envelope data")
        return None


# ---------------------------------------------------------------------------
# check_candidate_availability wrapper
# ---------------------------------------------------------------------------


async def evaluate_candidates(req: AvailabilityRequest) -> ToolResultEnvelope:
    """Invoke the check_candidate_availability tool and return the raw envelope.

    Args:
        req: The ``AvailabilityRequest`` to send (includes draft + hold preferences).

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    return await check_candidate_availability.ainvoke(req.model_dump(mode="json"))


def parse_availability(env: ToolResultEnvelope) -> AvailabilityResult | None:
    """Parse an ``AvailabilityResult`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`evaluate_candidates`.

    Returns:
        A validated ``AvailabilityResult`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None``.
    """
    if not env.ok or env.data is None:
        _log.warning(
            "evaluate_candidates returned non-ok or empty data: %s", env.status
        )
        return None
    try:
        return AvailabilityResult.model_validate(_payload(env.data, AvailabilityResult))
    except Exception:
        _log.exception("Failed to parse AvailabilityResult from envelope data")
        return None


# ---------------------------------------------------------------------------
# submit_confirmed_intent wrapper (02-J)
# ---------------------------------------------------------------------------


async def submit_intent_call(inp: SubmitConfirmedIntentInput) -> ToolResultEnvelope:
    """Invoke the submit_confirmed_intent tool and return the raw envelope.

    Passes ``inp`` verbatim — operation_id and idempotency_key are NEVER
    re-minted here (Inv-2).  The tool itself forwards them to the platform
    unchanged.

    Args:
        inp: The ``SubmitConfirmedIntentInput`` carrying the stable op+idem ids.

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    return await submit_confirmed_intent.ainvoke(inp.model_dump(mode="json"))


def parse_acceptance(env: ToolResultEnvelope) -> AcceptanceResult | None:
    """Parse an ``AcceptanceResult`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`submit_intent_call`.

    Returns:
        A validated ``AcceptanceResult`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None``.
    """
    if not env.ok or env.data is None:
        _log.warning("submit_intent_call returned non-ok or empty data: %s", env.status)
        return None
    try:
        return AcceptanceResult.model_validate(_payload(env.data, AcceptanceResult))
    except Exception:
        _log.exception("Failed to parse AcceptanceResult from envelope data")
        return None


# ---------------------------------------------------------------------------
# get_operation_status wrapper (02-J)
# ---------------------------------------------------------------------------


async def get_operation_status_call(operation_id: str) -> ToolResultEnvelope:
    """Invoke the get_operation_status tool and return the raw envelope.

    Args:
        operation_id: The stable operation identifier to poll.

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    return await get_operation_status.ainvoke({"operation_id": operation_id})


def parse_operation_status(env: ToolResultEnvelope) -> OperationStatusView | None:
    """Parse an ``OperationStatusView`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`get_operation_status_call`.

    Returns:
        A validated ``OperationStatusView`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None``.
    """
    if not env.ok or env.data is None:
        _log.warning(
            "get_operation_status_call returned non-ok or empty data: %s", env.status
        )
        return None
    try:
        return OperationStatusView.model_validate(
            _payload(env.data, OperationStatusView)
        )
    except Exception:
        _log.exception("Failed to parse OperationStatusView from envelope data")
        return None


# ---------------------------------------------------------------------------
# check_verification wrapper (02-K)
# ---------------------------------------------------------------------------


async def check_verification_call(
    verification_id: str,
    code: str | None = None,
) -> ToolResultEnvelope:
    """Invoke the check_verification tool and return the raw envelope.

    Passes ``verification_id`` always; adds ``code`` only when provided so the
    tool can either poll status (GET) or attempt grant issuance (POST).

    Args:
        verification_id: The opaque verification identifier assigned by the platform.
        code: The customer-supplied code to verify; ``None`` for a status poll.

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    params: dict = {"verification_id": verification_id}
    if code is not None:
        params["code"] = code
    return await check_verification.ainvoke(params)


def parse_verification_grant(env: ToolResultEnvelope) -> VerificationGrantView | None:
    """Parse a ``VerificationGrantView`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`check_verification_call`.

    Returns:
        A validated ``VerificationGrantView`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None``.
    """
    if not env.ok or env.data is None:
        _log.warning(
            "check_verification_call returned non-ok or empty data: %s", env.status
        )
        return None
    try:
        return VerificationGrantView.model_validate(
            _payload(env.data, VerificationGrantView)
        )
    except Exception:
        _log.exception("Failed to parse VerificationGrantView from envelope data")
        return None


# ---------------------------------------------------------------------------
# request_human_handoff wrapper (02-K)
# ---------------------------------------------------------------------------


async def request_human_handoff_call(req: HandoffRequest) -> ToolResultEnvelope:
    """Invoke the request_human_handoff tool and return the raw envelope.

    Passes ``req`` verbatim — handoff_idempotency_key is NEVER re-minted here
    (Inv-9).  The tool itself forwards it to the platform unchanged.

    Args:
        req: The ``HandoffRequest`` carrying the stable session + idempotency key.

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    return await request_human_handoff.ainvoke(req.model_dump(mode="json"))


def parse_handoff_result(env: ToolResultEnvelope) -> HandoffResult | None:
    """Parse a ``HandoffResult`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`request_human_handoff_call`.

    Returns:
        A validated ``HandoffResult`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None``.
    """
    if not env.ok or env.data is None:
        _log.warning(
            "request_human_handoff_call returned non-ok or empty data: %s", env.status
        )
        return None
    try:
        return HandoffResult.model_validate(_payload(env.data, HandoffResult))
    except Exception:
        _log.exception("Failed to parse HandoffResult from envelope data")
        return None


# ---------------------------------------------------------------------------
# Consent-gate wrappers (spec 14 §5.1/§5.2, D12)
# ---------------------------------------------------------------------------


async def resolve_consent_policy_call(channel: str) -> ToolResultEnvelope:
    """Invoke the resolve_consent_policy tool and return the raw envelope.

    Args:
        channel: The session origin channel (voice/sms/web/email).

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    return await resolve_consent_policy.ainvoke({"channel": channel})


def parse_consent_policy(env: ToolResultEnvelope) -> ConsentPolicyView | None:
    """Parse a ``ConsentPolicyView`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`resolve_consent_policy_call`.

    Returns:
        A validated ``ConsentPolicyView`` when ``env.ok`` and ``env.data`` is
        present, otherwise ``None`` (the consent gate fails closed on ``None``).
    """
    if not env.ok or env.data is None:
        _log.warning(
            "resolve_consent_policy_call returned non-ok or empty data: %s", env.status
        )
        return None
    try:
        return ConsentPolicyView.model_validate(_payload(env.data, ConsentPolicyView))
    except Exception:
        _log.exception("Failed to parse ConsentPolicyView from envelope data")
        return None


async def record_consent_call(inp: RecordConsentInput) -> ToolResultEnvelope:
    """Invoke the record_consent tool and return the raw envelope.

    Passes ``inp`` verbatim; the consent outcome is persisted onto the session
    row through the Booking API (single-writer rule, D12).

    Args:
        inp: The session id + captured ``ConsentState`` to persist.

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    return await record_consent.ainvoke(inp.model_dump(mode="json"))


def parse_consent_recorded(env: ToolResultEnvelope) -> ConsentRecordedResult | None:
    """Parse a ``ConsentRecordedResult`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`record_consent_call`.

    Returns:
        A validated ``ConsentRecordedResult`` when ``env.ok`` and ``env.data``
        is present, otherwise ``None`` (the consent gate fails closed).
    """
    if not env.ok or env.data is None:
        _log.warning(
            "record_consent_call returned non-ok or empty data: %s", env.status
        )
        return None
    try:
        return ConsentRecordedResult.model_validate(
            _payload(env.data, ConsentRecordedResult)
        )
    except Exception:
        _log.exception("Failed to parse ConsentRecordedResult from envelope data")
        return None


async def record_model_trace_call(inp: ModelTraceInput) -> ToolResultEnvelope:
    """Invoke the record_model_trace tool and return the raw envelope.

    ``inp.content`` MUST already be redacted (Inv-12); traces route to the
    stricter telemetry retention tier, never to app logs (spec 13 §25.3).

    Args:
        inp: The redacted model prompt/output to persist.

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    return await record_model_trace.ainvoke(inp.model_dump(mode="json"))


async def append_transcript_turn_call(inp: TranscriptTurnInput) -> ToolResultEnvelope:
    """Invoke the append_transcript_turn tool and return the raw envelope.

    ``inp.text`` MUST already be redacted (Inv-12); the API re-sanitizes and
    enforces the consent gate authoritatively (spec 14 §5.2).

    Args:
        inp: The redacted transcript turn to persist.

    Returns:
        The ``ToolResultEnvelope`` from the tool.
    """
    return await append_transcript_turn.ainvoke(inp.model_dump(mode="json"))


def parse_transcript_turn_persisted(
    env: ToolResultEnvelope,
) -> TranscriptTurnPersisted | None:
    """Parse a ``TranscriptTurnPersisted`` from an ok envelope, or return ``None``.

    Args:
        env: The ``ToolResultEnvelope`` returned by :func:`append_transcript_turn_call`.

    Returns:
        A validated ``TranscriptTurnPersisted`` when ``env.ok`` and ``env.data``
        is present, otherwise ``None`` (the persister treats it as best-effort —
        a failed persist never blocks the turn).
    """
    if not env.ok or env.data is None:
        _log.warning(
            "append_transcript_turn_call returned non-ok or empty data: %s", env.status
        )
        return None
    try:
        return TranscriptTurnPersisted.model_validate(
            _payload(env.data, TranscriptTurnPersisted)
        )
    except Exception:
        _log.exception("Failed to parse TranscriptTurnPersisted from envelope data")
        return None
