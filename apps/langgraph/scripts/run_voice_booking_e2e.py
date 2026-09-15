"""Opt-in live end-to-end runner: caller AUDIO -> real .NET API -> real DB row.

The voice sibling of ``run_text_booking_e2e.py``, and the thing that closes the
"no media plane" gap in ``docs/end-to-end-handoff.md``.  Nothing here is mocked:
audio really goes through the Azure realtime transcription socket, the
recognised transcript really drives the §8.3 graph, the graph really calls the
Booking API, and the agent's reply is really synthesized by ``gpt-4o-mini-tts``
and written to a playable WAV.

NOT part of the automated suite.  The pytest suites stay 100% offline.

Where the caller's audio comes from
    A real call has a person on the line; a repeatable script cannot.  So the
    caller's lines are SYNTHESIZED with the same Azure TTS deployment, at a
    different voice from the agent's, and fed to STT as if they had arrived
    from a media plane.  That is not a shortcut around the hard part — it means
    every byte the recogniser sees has been through a real vocoder, and it
    exercises the resampler in both directions (TTS emits 24 kHz; the realtime
    socket wants 16 kHz; the agent's reply goes out at whatever
    ``--egress`` asks for, 8 kHz mu-law by default).

    Pass ``--caller-audio turn.wav`` to drive a turn from a real recording
    instead.  The pipeline cannot tell the difference.

    Audio is delivered in 100 ms packets and paced to wall-clock by default,
    because the service's VAD endpoints on real time: feeding it half-second
    bursts makes the gaps BETWEEN packets look like the caller pausing, and a
    single spoken booking comes back in fragments.  ``--no-realtime`` skips the
    pacing when you only care about the plumbing.

One final per turn
    ``STTAdapter`` commits the FIRST final of a stream and stops, per Inv-1.
    So if the server-side VAD splits a caller's sentence in two, the tail is
    not part of that turn — and on a reused session it used to surface as the
    next turn's first transcript (the provider now drains it instead).  That
    makes the endpointing window the knob that matters: ``--silence-ms``
    defaults to 900 rather than the library default of 500 so a spoken booking
    stays one utterance.

What runs
    1. ``POST /api/channels/telephony`` mints a signed ``TenantContext`` from
       the seeded DID — the same ingress the text runner uses.
    2. One :class:`Pipeline` is built per turn over ONE pre-warmed realtime STT
       session, one TTS provider, one transcoding sink and one
       :class:`GraphAgentPort` bound to a checkpointed graph.
    3. Each turn's caller audio is streamed in; every frame the pipeline emits
       is printed; the agent's speech lands in ``--out-dir`` as one WAV per
       turn.
    4. The final graph state is reported, with the operation id to check in
       Postgres.

Prerequisites (see docs/end-to-end-handoff.md):

    docker compose up -d
    # API on http://localhost:8000 with Development migrations applied
    docker exec -i cordia-postgres psql -U cordia -d Cordia_API \
        < apps/api/scripts/seed-dev-tenant.sql
    docker exec -i cordia-postgres psql -U cordia -d Cordia_API \
        < apps/api/scripts/seed-dev-venue.sql

    The venue seed is NOT optional on voice.  It is the only thing that populates
    `tenant_resources`, which is the closed set `resolve_tenant_resources` matches
    a spoken name against — without it "a table in the Fitzroy room" resolves to
    nothing, no location is ever captured, and the call loops on the same question.
    It also supplies the names read-back speaks, and speakable service ids that
    survive a recogniser (`svc_001` comes back as "SVC zero zero one").

    export CORDIA_API_BASE_URL=http://localhost:8000
    # plus apps/langgraph/.env (Azure + CORDIA_TENANT_CONTEXT_SIGNING_KEYS)

Run:

    cd apps/langgraph && uv run python scripts/run_voice_booking_e2e.py
"""

from __future__ import annotations

import argparse
import asyncio
import contextlib
import json
import math
import os
import sys
import time
import uuid
from pathlib import Path
from typing import Any


try:  # optional: makes `uv run python scripts/...` pick up apps/langgraph/.env
    from dotenv import load_dotenv
except ImportError:  # pragma: no cover - dotenv is a dev convenience only
    load_dotenv = None


DEFAULT_API_BASE_URL = "http://localhost:8000"
DEFAULT_DID = "+61400000001"
#: The caller's own number, posted to the telephony ingress as `from` and then
#: carried into the graph input as the customer's phone.  It is the same number
#: in both places on purpose: the ingress deliberately ignores `from` when it
#: resolves the tenant (Inv-13, caller-mutable), so if the runner did not also
#: hand it to the graph the booking would be created with no guest attached and
#: `GET /api/bookings?phone_e164=...` would answer "none".
CALLER_PHONE_E164 = "+61411111111"


# The same three turns the text runner uses, so a discrepancy between the
# channels is a channel difference and not a script difference.
DEFAULT_TURNS: tuple[str, ...] = (
    "Hello?",
    (
        "Yes, that's fine. I'd like to book a table — service svc_001 at "
        "location loc_001, for 4 people, tomorrow at 7pm, and we'll need a "
        "high chair."
    ),
    "Yes, that's right, go ahead.",
)

# A different voice for the caller than the agent, so a listener can tell the
# two sides of the recording apart.
CALLER_VOICE = "onyx"

EGRESS_PRESETS = {
    "pstn": ("8 kHz G.711 mu-law — what a telephone leg carries", 8000, "mulaw"),
    "wideband": ("16 kHz linear PCM — WebRTC/browser", 16000, "pcm16"),
    "native": ("24 kHz linear PCM — no conversion", 24000, "pcm16"),
}


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
    """Render a value compactly, truncating anything unreadably long."""
    try:
        text = json.dumps(value, default=str)
    except (TypeError, ValueError):
        text = repr(value)
    return text if len(text) <= 300 else text[:300] + "..."


async def mint_tenant_context(base_url: str, did: str) -> dict[str, Any]:
    """POST the telephony webhook and return the minted TenantContext response.

    Args:
        base_url: Booking API base URL.
        did: The called DID — must match an ``active`` row in
            ``tenant_channel_endpoints`` or the API answers 422.

    Returns:
        The parsed ``TenantContextResponse`` body.

    Raises:
        RuntimeError: if the API does not answer 200.
    """
    import httpx

    async with httpx.AsyncClient(base_url=base_url, timeout=15.0) as http:
        response = await http.post(
            "/api/channels/telephony",
            json={
                "to": did,
                "from": CALLER_PHONE_E164,
                "caller_name": "Voice E2E",
            },
        )
    if response.status_code != 200:
        raise RuntimeError(
            f"telephony webhook returned {response.status_code}: {response.text}\n"
            "Is the API running, and has apps/api/scripts/seed-dev-tenant.sql "
            "been applied?"
        )
    return response.json()


async def synthesize_caller_line(settings: Any, text: str, *, voice: str) -> bytes:
    """Render one caller utterance to 24 kHz PCM using the TTS deployment.

    This is the stand-in for a person on the line.  It deliberately goes
    through the real speech route rather than generating a tone, so the
    recogniser is fed something a recogniser can actually recognise.

    Args:
        settings: Resolved :class:`AzureAudioSettings`.
        text: What the caller says.
        voice: Azure voice preset — a different one from the agent's.

    Returns:
        24 kHz 16-bit mono PCM.

    Raises:
        RuntimeError: if the speech route answers non-2xx.
    """
    import httpx

    body = {
        "model": settings.tts_deployment,
        "input": text,
        "voice": voice,
        "response_format": "pcm",
    }
    async with httpx.AsyncClient(timeout=60.0) as http:
        response = await http.post(
            settings.speech_url(), headers=settings.auth_headers(), json=body
        )
    if response.status_code >= 300:
        raise RuntimeError(
            f"caller-audio synthesis failed with HTTP {response.status_code}"
        )
    return response.content


def load_caller_audio(path: Path, target_rate_hz: int) -> bytes:
    """Read a WAV recording and resample it to the recogniser's rate.

    Args:
        path: A 16-bit mono WAV file.
        target_rate_hz: Rate the realtime socket expects.

    Returns:
        16-bit mono PCM at *target_rate_hz*.
    """
    from cordia_agent.streaming.providers.audio_codec import PcmResampler, wav_to_pcm16

    pcm, rate = wav_to_pcm16(path.read_bytes())
    if rate == target_rate_hz:
        return pcm
    return PcmResampler(source_rate_hz=rate, target_rate_hz=target_rate_hz).resample(
        pcm
    )


#: Pacing granularity for the caller's audio, in milliseconds.  This is NOT
#: ``realtime_send_chunk_bytes`` — that is a cap on how much PCM rides in one
#: append frame, and using it as the pacing unit was a bug worth remembering:
#: at its 500 ms default the stream arrived as half-second bursts with
#: half-second gaps, and the SERVICE's VAD read those gaps as the caller
#: pausing.  A 15-second booking came back split into fragments.  A real media
#: plane delivers ~20-100 ms packets; 100 ms is comfortably inside that.
PACING_CHUNK_MS = 100


async def stream_pcm(
    pcm: bytes, *, realtime: bool, sample_rate_hz: int, tail_seconds: float
):
    """Yield *pcm* as a media plane would: small packets, optionally paced.

    Args:
        pcm: The caller audio.
        realtime: When True, sleep between packets so audio arrives no faster
            than wall-clock — which is what a live call does, and what makes
            the service's server-side VAD endpoint the way it would on a call.
        sample_rate_hz: Rate of *pcm*, used to size and pace the packets.
        tail_seconds: Trailing silence appended after the last word.

    Yields:
        Packets of *pcm*, then silence.
    """
    chunk_bytes = max(2, (sample_rate_hz * 2 * PACING_CHUNK_MS) // 1000)
    seconds_per_chunk = chunk_bytes / (sample_rate_hz * 2)

    for offset in range(0, len(pcm), chunk_bytes):
        yield pcm[offset : offset + chunk_bytes]
        if realtime:
            await asyncio.sleep(seconds_per_chunk)

    # A caller does not stop dead at the last word, and the server-side VAD
    # endpoints on the silence that follows.  Without enough of it the service
    # never decides the utterance ended, and the turn dies on the drain window
    # instead of on a transcript.
    silence = b"\x00" * chunk_bytes
    for _ in range(math.ceil(tail_seconds / seconds_per_chunk)):
        yield silence
        if realtime:
            await asyncio.sleep(seconds_per_chunk)


async def main() -> int:
    """Run the full audio-driven booking call and report the outcome."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-base-url", default=None)
    parser.add_argument("--did", default=DEFAULT_DID)
    parser.add_argument(
        "--turn",
        action="append",
        dest="turns",
        help="Caller line to synthesize; repeatable. Defaults to the text runner's three.",
    )
    parser.add_argument(
        "--caller-audio",
        action="append",
        dest="caller_audio",
        help="WAV file to use for a turn instead of synthesizing it; repeatable.",
    )
    parser.add_argument(
        "--egress",
        choices=sorted(EGRESS_PRESETS),
        default="pstn",
        help="Carriage format the agent's speech is converted to.",
    )
    parser.add_argument(
        "--out-dir",
        default=".tmp/voice-e2e",
        help="Where per-turn agent-speech WAVs are written.",
    )
    parser.add_argument(
        "--no-realtime",
        action="store_true",
        help="Push audio as fast as possible instead of pacing it to wall-clock.",
    )
    parser.add_argument(
        "--silence-ms",
        type=int,
        default=900,
        help=(
            "Trailing silence the server-side VAD needs before it endpoints. "
            "Higher than the 500ms default on purpose: a consumer takes ONE "
            "final per turn, so a sentence the VAD splits in two loses its "
            "tail. 900ms keeps a spoken booking together."
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
    for key in ("tenant_id", "channel_endpoint_id", "session_id", "correlation_id"):
        print(f"  {key:20s}= {claims[key]}")

    # Imported here, after CORDIA_API_BASE_URL is exported, so the module-scope
    # bootstraps in cordia_agent.graph see the configured environment.
    import httpx
    from langgraph.checkpoint.memory import InMemorySaver

    from cordia_agent.graph import build_agent_graph
    from cordia_agent.slots.capture import get_slot_extractor
    from cordia_agent.streaming.graph_agent_port import (
        AgentSessionContext,
        GraphAgentPort,
    )
    from cordia_agent.streaming.pipeline import Pipeline, PipelineSession
    from cordia_agent.streaming.providers.audio_config import audio_settings_from_env
    from cordia_agent.streaming.providers.azure_realtime_stt import (
        AzureRealtimeSTTProvider,
    )
    from cordia_agent.streaming.providers.azure_tts import AzureTtsProvider
    from cordia_agent.streaming.providers.media_plane import (
        AudioEgress,
        TranscodingAudioSink,
        WavFileMediaPlane,
    )
    from cordia_agent.streaming.turn_manager import TurnManager
    from cordia_agent.streaming_models import (
        AgentTextFinalFrame,
        ToolProgressFrame,
        TranscriptFinalFrame,
        TranscriptPartialFrame,
        TtsAudioDeltaFrame,
    )
    from cordia_agent.tools.client import get_tool_client

    get_tool_client()  # raises if the CORDIA_API_BASE_URL bootstrap did not register
    get_slot_extractor()  # raises if the slot-extractor bootstrap did not register

    # Applied before settings are resolved so the value reaches the session
    # config the realtime socket is opened with.
    os.environ["CORDIA_AUDIO_REALTIME_SILENCE_DURATION_MS"] = str(args.silence_ms)

    settings = audio_settings_from_env()
    print(f"Server-VAD endpointing silence: {settings.realtime_silence_duration_ms}ms")
    description, egress_rate, egress_encoding = EGRESS_PRESETS[args.egress]
    egress = AudioEgress(sample_rate_hz=egress_rate, encoding=egress_encoding)
    print(f"Media plane egress: {args.egress} — {description}")

    session_id = claims["session_id"]
    out_dir = Path(args.out_dir)
    media_plane = WavFileMediaPlane(out_dir, filename_prefix=session_id[:12])
    sink = TranscodingAudioSink(delivery=media_plane, egress=egress)

    graph = build_agent_graph(checkpointer=InMemorySaver())
    agent = GraphAgentPort(
        graph,
        session=AgentSessionContext(
            signed_tenant_context=minted["tenant_context"],
            session_id=session_id,
            tenant_id=claims["tenant_id"],
            correlation_id=claims["correlation_id"],
            # The number the ingress was told about, handed to the graph as the
            # customer so the booking ends up with a guest and a place to
            # notify.  Contact hint only: the tenant came from the called DID.
            caller_phone_e164=CALLER_PHONE_E164,
            channel="voice",
            language="en-AU",
        ),
    )

    lines = list(args.turns or DEFAULT_TURNS)
    recordings = [Path(p) for p in (args.caller_audio or [])]

    exit_code = 1
    async with httpx.AsyncClient(timeout=None) as http:
        tts = AzureTtsProvider(settings, http, sink)
        stt = AzureRealtimeSTTProvider(settings)

        # §6.6: pay the handshake ONCE, at call setup, while a real caller
        # would still be hearing the greeting.  Every turn below reuses it.
        warm_started = time.perf_counter()
        await stt.prewarm()
        print(
            f"Realtime STT session pre-warmed in "
            f"{time.perf_counter() - warm_started:.2f}s (paid once, not per turn)"
        )

        try:
            # One TurnManager for the whole call: seq is monotonic per SESSION,
            # across every turn and every frame source.
            turn_manager = TurnManager()

            for index, line in enumerate(lines, start=1):
                turn_id = f"turn-{index}-{uuid.uuid4().hex[:8]}"

                if index <= len(recordings):
                    source = recordings[index - 1]
                    print(f"\n=== TURN {index} — caller audio from {source} ===")
                    caller_pcm = load_caller_audio(
                        source, settings.input_sample_rate_hz
                    )
                else:
                    print(f"\n=== TURN {index} — caller says: {line!r} ===")
                    native = await synthesize_caller_line(
                        settings, line, voice=CALLER_VOICE
                    )
                    from cordia_agent.streaming.providers.audio_codec import (
                        PcmResampler,
                    )

                    caller_pcm = PcmResampler(
                        source_rate_hz=settings.tts_sample_rate_hz,
                        target_rate_hz=settings.input_sample_rate_hz,
                    ).resample(native)

                seconds = len(caller_pcm) / (settings.input_sample_rate_hz * 2)
                print(
                    f"    {len(caller_pcm)} bytes of "
                    f"{settings.input_sample_rate_hz} Hz PCM ({seconds:.1f}s)"
                )

                pipeline = Pipeline(
                    stt_provider=stt,
                    tts_provider=tts,
                    agent=agent,
                    turn_manager=turn_manager,
                )
                pipeline_session = PipelineSession(
                    tenant_id=claims["tenant_id"],
                    session_id=session_id,
                    turn_id=turn_id,
                    correlation_id=claims["correlation_id"],
                )

                started = time.perf_counter()
                audio_chunks = 0
                progress_frames = 0
                said: list[str] = []
                heard: str | None = None

                chunks = stream_pcm(
                    caller_pcm,
                    realtime=not args.no_realtime,
                    sample_rate_hz=settings.input_sample_rate_hz,
                    # Enough silence for the VAD to endpoint on, plus headroom.
                    tail_seconds=args.silence_ms / 1000 + 1.5,
                )
                async with contextlib.aclosing(
                    pipeline.run(chunks, session=pipeline_session)
                ) as frames:
                    async for frame in frames:
                        elapsed = time.perf_counter() - started
                        if isinstance(frame, TranscriptPartialFrame):
                            label = frame.text or "<speech onset>"
                            print(f"  [{elapsed:5.2f}s] partial   {label!r}")
                        elif isinstance(frame, TranscriptFinalFrame):
                            heard = frame.text
                            print(
                                f"  [{elapsed:5.2f}s] HEARD     {frame.text!r} "
                                f"(asr_confidence={frame.asr_confidence})"
                            )
                        elif isinstance(frame, ToolProgressFrame):
                            progress_frames += 1
                            # One line per tool, not one per tick: a 12s slot
                            # extraction is 48 frames and drowns the trace.
                            if progress_frames == 1 or frame.elapsed_ms % 5000 < 300:
                                print(
                                    f"  [{elapsed:5.2f}s] progress  {frame.tool_name} "
                                    f"({frame.elapsed_ms}ms)"
                                )
                        elif isinstance(frame, AgentTextFinalFrame):
                            said.append(frame.text)
                            print(f"  [{elapsed:5.2f}s] SAID      {frame.text!r}")
                        elif isinstance(frame, TtsAudioDeltaFrame):
                            audio_chunks += 1

                if heard is None:
                    print("  (no transcript was committed for this turn)")
                print(
                    f"  -> {audio_chunks} audio chunk(s) placed on the media plane, "
                    f"{progress_frames} progress frame(s), "
                    f"in {time.perf_counter() - started:.2f}s"
                )
        finally:
            await stt.aclose()
            await media_plane.aclose()

        snapshot = await graph.aget_state(agent.config)
        state = snapshot.values

        print("\n=== RESULT ===")
        for key in (
            "tenant_id",
            "terminal_state",
            "operation_id",
            "idempotency_key",
            "submission_status",
            "availability_mode",
            "disclosures_presented",
        ):
            print(f"  {key} = {_fmt(state.get(key))}")

        print("\n=== AGENT SPEECH ON THE MEDIA PLANE ===")
        if not media_plane.files:
            print("  (nothing was synthesized)")
        for turn_id, path in media_plane.files.items():
            size = path.stat().st_size
            print(f"  {turn_id:24s} {path}  ({size} bytes)")

        if state.get("operation_id") and state.get("terminal_state") == "submitted":
            print("\nOK: a voice call produced a durably accepted operation.")
            print(
                "    Verify the row:\n"
                "    docker exec -i cordia-postgres psql -U cordia -d Cordia_API "
                '-c "select operation_id, status from booking_operations '
                'order by created_at desc limit 3;"'
            )
            exit_code = 0
        else:
            print("\nFAILED: no operation was durably accepted (see the trace above).")

    return exit_code


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
