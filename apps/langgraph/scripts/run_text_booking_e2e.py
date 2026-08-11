"""Opt-in live end-to-end runner: text transcript -> real .NET API -> real DB row.

NOT part of the automated suite.  The pytest suites stay 100% offline; this
script is the only thing that makes live calls (Azure OpenAI for slot capture,
the local Booking API over HTTP, and through it Postgres).

What it does, in order:

1. POSTs the telephony ingress webhook (``POST /api/channels/telephony``) with
   the seeded DID, which resolves the tenant and mints a signed compact-JWS
   ``TenantContext`` — exactly the ingress the voice pipeline would use, minus
   the audio.
2. Compiles the real agent graph with an in-memory checkpointer and drives it
   turn by turn with ``transcript_text`` / ``transcript_is_final=True``.  A
   dialog spans several turns (details -> read-back -> confirmation) and each
   turn is one graph invocation, so the checkpointer is what carries state
   between them.
3. Streams ``stream_mode="updates"`` so every node's state delta is printed as
   it happens.  The graph ends each turn itself now (clarify, an unconfirmed
   read-back and the consent notice all END), so the driver simply runs each
   turn to completion — it no longer has to break out of ``astream`` at the
   confirmation gate, and it no longer injects ``disclosures_presented``
   through ``aupdate_state``: ``consent_gate`` and ``read_back`` record the
   disclosures they actually present.
4. Reports the final terminal state, ``operation_id`` and submission status.

Prerequisites (see docs/text-booking-e2e.md):

    docker compose up -d
    # API on http://localhost:8000 with Development migrations applied
    psql "postgres://cordia:localdev@localhost:5432/Cordia_API" \
        -f apps/api/scripts/seed-dev-tenant.sql

    export CORDIA_API_BASE_URL=http://localhost:8000
    # plus apps/langgraph/.env (Azure + CORDIA_TENANT_CONTEXT_SIGNING_KEYS)

Run:

    cd apps/langgraph && uv run python scripts/run_text_booking_e2e.py
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
import uuid
from pathlib import Path
from typing import Any


try:  # optional: makes `uv run python scripts/...` pick up apps/langgraph/.env
    from dotenv import load_dotenv
except ImportError:  # pragma: no cover - dotenv is a dev convenience only
    load_dotenv = None


DEFAULT_API_BASE_URL = "http://localhost:8000"
DEFAULT_DID = "+61400000001"

# Turn 1 opens the session: the consent gate resolves the jurisdiction policy,
# emits the recording notice and ENDs the turn awaiting a reply (§5.1).
DEFAULT_TURN_1 = "Hello?"

# Turn 2 both answers the consent notice AND states the booking.  Answering
# consent on its own turn would leave the following turn with no booking intent,
# and the §8.3 topology has no END on the "no draft yet" path — capture_fields
# and clarify cycle until the recursion limit.
#
# The identifiers are spoken explicitly on purpose: the extractor is forbidden
# from inventing an id from a description ("a haircut"), because a hallucinated
# service_id would be submitted verbatim.
DEFAULT_TURN_2 = (
    "Yes, that's fine. I'd like to book a table — service svc_001 at "
    "location loc_001, for 4 people, tomorrow at 7pm, and we'll need a "
    "high chair."
)

DEFAULT_TURN_3 = "Yes, that's right, go ahead."


def _quieten_openai_serializer_warnings() -> None:
    """Suppress the OpenAI SDK's benign Pydantic union-serialization warnings.

    The Responses surface declares its ``output`` field as a wide union; when a
    structured-output call comes back, pydantic-core notes that the value did
    not match most members of that union and emits a warning PER MEMBER. It is
    noise from a third-party model — dozens of lines per model call — and it
    drowns the console. Matched narrowly on the message so a genuine warning
    from our own models still gets through.
    """
    import warnings

    warnings.filterwarnings(
        "ignore",
        message=r"Pydantic serializer warnings",
        category=UserWarning,
    )


def _fmt(value: Any) -> str:
    """Render a state delta compactly, truncating anything unreadably long."""
    try:
        text = json.dumps(value, default=str)
    except (TypeError, ValueError):
        text = repr(value)
    return text if len(text) <= 400 else text[:400] + "..."


async def mint_tenant_context(base_url: str, did: str) -> dict[str, Any]:
    """POST the telephony webhook and return the minted TenantContext response.

    Args:
        base_url: Booking API base URL.
        did: The called DID — must match an ``active`` row in
            ``tenant_channel_endpoints`` or the API answers 422.

    Returns:
        The parsed ``TenantContextResponse`` body (``tenant_context`` + ``claims``).

    Raises:
        RuntimeError: if the API does not answer 200.
    """
    import httpx

    async with httpx.AsyncClient(base_url=base_url, timeout=15.0) as http:
        response = await http.post(
            "/api/channels/telephony",
            json={"to": did, "from": "+61411111111", "caller_name": "E2E Runner"},
        )
    if response.status_code != 200:
        raise RuntimeError(
            f"telephony webhook returned {response.status_code}: {response.text}\n"
            "Is the API running, and has apps/api/scripts/seed-dev-tenant.sql "
            "been applied?"
        )
    return response.json()


async def run_turn(
    graph: Any,
    config: dict[str, Any],
    payload: dict[str, Any] | None,
    *,
    label: str,
) -> None:
    """Stream one graph turn to its natural END, printing each update as it lands.

    Args:
        graph: The compiled agent graph.
        config: LangGraph config carrying the checkpointer ``thread_id``.
        payload: The ``AgentInputState`` payload for this turn, or ``None`` to
            resume a turn that was interrupted (only used by
            ``--stub-availability``).
        label: Human label for the console header.
    """
    print(f"\n=== {label} ===")
    if payload is not None:
        print(f"    transcript: {payload.get('transcript_text')!r}")
    async for chunk in graph.astream(payload, config=config, stream_mode="updates"):
        for node, update in chunk.items():
            keys = sorted(update) if isinstance(update, dict) else []
            print(f"  [node] {node}")
            for key in keys:
                print(f"          {key} = {_fmt(update[key])}")


async def main() -> int:
    """Run the full text-driven booking flow and report the outcome."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-base-url", default=None)
    parser.add_argument("--did", default=DEFAULT_DID)
    parser.add_argument("--turn1", default=DEFAULT_TURN_1)
    parser.add_argument("--turn2", default=DEFAULT_TURN_2)
    parser.add_argument("--turn3", default=DEFAULT_TURN_3)
    parser.add_argument(
        "--stub-availability",
        action="store_true",
        help=(
            "Inject one candidate after check_candidates instead of calling the "
            "API. Not needed any more — the API implements POST "
            "/api/availability/candidates and the real path reaches read_back on "
            "its own. Kept as an escape hatch for driving the rest of the chain "
            "(read_back -> confirmation -> submit_intent -> POST /api/bookings) "
            "when the availability route itself is under investigation."
        ),
    )
    args = parser.parse_args()

    _quieten_openai_serializer_warnings()

    if load_dotenv is not None:
        load_dotenv(Path(__file__).resolve().parent.parent / ".env")

    base_url = (
        args.api_base_url
        or os.environ.get("CORDIA_API_BASE_URL", "").strip()
        or DEFAULT_API_BASE_URL
    )
    # The graph registers its ToolClient from this variable at import time, so
    # it must be set BEFORE cordia_agent.graph is imported below.
    os.environ["CORDIA_API_BASE_URL"] = base_url

    print(f"API base URL: {base_url}")

    minted = await mint_tenant_context(base_url, args.did)
    claims = minted["claims"]
    print("Minted TenantContext:")
    print(f"  tenant_id           = {claims['tenant_id']}")
    print(f"  channel_endpoint_id = {claims['channel_endpoint_id']}")
    print(f"  session_id          = {claims['session_id']}")
    print(f"  correlation_id      = {claims['correlation_id']}")
    print(f"  expires_at          = {claims['expires_at']}")

    # Imported here, after CORDIA_API_BASE_URL is exported, so the module-scope
    # bootstraps in cordia_agent.graph see the configured environment.
    from langgraph.checkpoint.memory import InMemorySaver

    from cordia_agent.graph import build_agent_graph
    from cordia_agent.slots.capture import get_slot_extractor
    from cordia_agent.tools.client import get_tool_client

    get_tool_client()  # raises if CORDIA_API_BASE_URL bootstrap did not register
    get_slot_extractor()  # raises if the slot-extractor bootstrap did not register

    graph = build_agent_graph(checkpointer=InMemorySaver())
    config = {
        "configurable": {"thread_id": claims["session_id"]},
        "recursion_limit": 50,
    }

    base_payload = {
        "signed_tenant_context": minted["tenant_context"],
        "session_id": claims["session_id"],
        "source": {"channel": "voice", "language": "en-AU"},
        "transcript_is_final": True,
    }

    def turn(text: str) -> dict[str, Any]:
        return {
            **base_payload,
            "transcript_text": text,
            "current_turn_id": f"turn-{uuid.uuid4()}",
        }

    await run_turn(
        graph,
        config,
        turn(args.turn1),
        label="TURN 1 — session opens, consent notice",
    )

    async def patch_availability() -> None:
        """Bypass the API's availability route with a fixed candidate.

        ``as_node`` re-runs ``route_after_check_candidates`` over the patched
        values, so the branch is recomputed rather than left on its
        "no candidates ⇒ back to capture_fields" leg.
        """
        state = (await graph.aget_state(config)).values
        starts_at = state["draft"].request.starts_at
        print("  (--stub-availability: substituting the missing availability service)")
        await graph.aupdate_state(
            config,
            {
                "availability_mode": "informational",
                "availability_invalid_fields": [],
                "availability_candidates": [
                    {
                        "starts_at": starts_at.isoformat(),
                        "duration_minutes": 90,
                        "availability_mode": "informational",
                    }
                ],
            },
            as_node="check_candidates",
        )

    async def booking_turn(text: str, *, label: str) -> None:
        """Run one booking turn, patching availability mid-turn when stubbing."""
        await run_turn(graph, config, turn(text), label=label)
        if args.stub_availability:
            await patch_availability()
            await run_turn(
                graph,
                config,
                None,
                label=f"{label} (resumed past availability)",
            )

    await booking_turn(args.turn2, label="TURN 2 — consent reply + booking details")
    await booking_turn(args.turn3, label="TURN 3 — confirmation")

    snapshot = await graph.aget_state(config)
    state = snapshot.values

    print("\n=== RESULT ===")
    for key in (
        "tenant_id",
        "terminal_state",
        "operation_id",
        "idempotency_key",
        "submission_status",
        "submission_message",
        "availability_mode",
        "read_back_text",
        "disclosures_presented",
    ):
        print(f"  {key} = {_fmt(state.get(key))}")

    draft = state.get("draft")
    if draft is not None:
        request = draft.request if hasattr(draft, "request") else draft["request"]
        print(f"  draft.request = {_fmt(request.model_dump(mode='json'))}")

    if state.get("operation_id") and state.get("terminal_state") == "submitted":
        print("\nOK: the operation was accepted by the API.")
        print(
            "    Verify the row:\n"
            '    psql "postgres://cordia:localdev@localhost:5432/Cordia_API" '
            '-c "select operation_id, tenant_id, status from booking_operations '
            'order by created_at desc limit 5;"'
        )
        return 0

    print("\nFAILED: no operation was durably accepted (see the node trace above).")
    return 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
