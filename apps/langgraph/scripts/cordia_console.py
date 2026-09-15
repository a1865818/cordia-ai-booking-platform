"""Interactive console for driving a Cordia session by hand, on either channel.

The two e2e runners (``run_text_booking_e2e.py``, ``run_voice_booking_e2e.py``)
replay a fixed script and exit, which is what you want in CI and exactly what
you do not want while you are poking at behaviour: changing one thing means
editing the script and paying the whole run again.

This is the same stack with a prompt in front of it.  Type a line, watch the
turn, type another.  ``/voice`` flips the SAME session onto the audio path —
your line is synthesized, recognised by the real STT socket, driven through the
graph, and spoken back through TTS onto the media plane — so you can compare
"what the graph does with this sentence" against "what the graph does with this
sentence after a recogniser has been at it", which is where the interesting
differences live.

Everything is one session: the checkpointer thread, the tenant context, the
pre-warmed STT socket and the media plane are all created once and shared, so
switching channels mid-conversation continues the same booking.

Prerequisites are the handoff doc's (docker, the API on :8000, the seed applied,
apps/langgraph/.env).  Run:

    cd apps/langgraph && uv run python scripts/cordia_console.py
    cd apps/langgraph && uv run python scripts/cordia_console.py --voice

Commands:

    /voice          drive following turns through the audio pipeline
    /text           drive following turns as text (the default)
    /state [key]    dump the checkpointed graph state, or one key of it
    /audio          list the WAVs written for this session
    /reset          start a fresh session (new tenant context, new thread)
    /help, /quit
"""

from __future__ import annotations

import argparse
import asyncio
import contextlib
import json
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
#: The console has no real ANI; this stands in as the caller's contact number.
CALLER_PHONE_E164 = "+61411111111"
DEFAULT_DID = "+61400000001"

# A different voice for "you" than the agent's, so a recording of a session has
# two distinguishable sides.
CALLER_VOICE = "onyx"

# Delivered in 100 ms packets: the service's VAD endpoints on real time, and
# feeding it half-second bursts makes the gaps between packets look like the
# caller pausing.  See run_voice_booking_e2e.py.
PACING_CHUNK_MS = 100

BANNER = """
Cordia console — {channel} channel, session {session}
Type a line to take a turn.  /help for commands, /quit to leave.
"""


def _fmt(value: Any, *, limit: int = 400) -> str:
    """Render a value compactly for the console."""
    try:
        text = json.dumps(value, default=str, indent=2)
    except (TypeError, ValueError):
        text = repr(value)
    return text if len(text) <= limit else text[:limit] + "…"


class Console:
    """One interactive session across both channels."""

    def __init__(self, *, base_url: str, did: str, out_dir: Path, silence_ms: int):
        self.base_url = base_url
        self.did = did
        self.out_dir = out_dir
        self.silence_ms = silence_ms
        self.voice = False
        self._stt: Any = None
        self._tts: Any = None
        self._media: Any = None
        self._http: Any = None
        self._settings: Any = None

    # -- session lifecycle ---------------------------------------------------

    async def open(self) -> None:
        """Mint a tenant context and build the graph, port and audio stack."""
        import httpx
        from langgraph.checkpoint.memory import InMemorySaver

        from cordia_agent.graph import build_agent_graph
        from cordia_agent.streaming.graph_agent_port import (
            AgentSessionContext,
            GraphAgentPort,
        )

        async with httpx.AsyncClient(base_url=self.base_url, timeout=15.0) as http:
            response = await http.post(
                "/api/channels/telephony",
                json={
                    "to": self.did,
                    "from": CALLER_PHONE_E164,
                    "caller_name": "Console",
                },
            )
        if response.status_code != 200:
            raise RuntimeError(
                f"telephony webhook returned {response.status_code}: {response.text}\n"
                "Is the API running, and has apps/api/scripts/seed-dev-tenant.sql "
                "been applied?"
            )
        minted = response.json()
        self.claims = minted["claims"]
        self.session_id = self.claims["session_id"]

        self.graph = build_agent_graph(checkpointer=InMemorySaver())
        self.agent = GraphAgentPort(
            self.graph,
            session=AgentSessionContext(
                # The console's synthetic caller.  A booking with no guest cannot
                # be found by phone or notified; this is a contact hint only and
                # never resolves the tenant (Inv-13).
                caller_phone_e164=CALLER_PHONE_E164,
                signed_tenant_context=minted["tenant_context"],
                session_id=self.session_id,
                tenant_id=self.claims["tenant_id"],
                correlation_id=self.claims["correlation_id"],
                channel="voice",
                language="en-AU",
            ),
        )

    async def _ensure_audio(self) -> None:
        """Build the audio stack on first use, and only then.

        A text-only session should not open a socket to Azure or synthesize a
        thing, so none of this is constructed until ``/voice`` is used.
        """
        if self._stt is not None:
            return

        import httpx

        from cordia_agent.streaming.providers.audio_config import (
            audio_settings_from_env,
        )
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

        os.environ["CORDIA_AUDIO_REALTIME_SILENCE_DURATION_MS"] = str(self.silence_ms)
        self._settings = audio_settings_from_env()

        self._media = WavFileMediaPlane(
            self.out_dir, filename_prefix=self.session_id[:12]
        )
        sink = TranscodingAudioSink(delivery=self._media, egress=AudioEgress.pstn())
        self._http = httpx.AsyncClient(timeout=None)
        self._tts = AzureTtsProvider(self._settings, self._http, sink)
        self._stt = AzureRealtimeSTTProvider(self._settings)
        self._turn_manager = TurnManager()

        started = time.perf_counter()
        await self._stt.prewarm()
        print(f"  (STT session pre-warmed in {time.perf_counter() - started:.2f}s)")

    async def close(self) -> None:
        """Release the audio stack, if it was ever built."""
        if self._stt is not None:
            await self._stt.aclose()
        if self._media is not None:
            await self._media.aclose()
        if self._http is not None:
            await self._http.aclose()

    # -- turns ---------------------------------------------------------------

    async def take_turn(self, text: str) -> None:
        """Run one turn on the currently selected channel."""
        if self.voice:
            await self._voice_turn(text)
        else:
            await self._text_turn(text)

    async def _text_turn(self, text: str) -> None:
        """Feed the transcript straight to the graph, as the text runner does."""
        from cordia_agent.state import StreamFrameType
        from cordia_agent.streaming_models import (
            AgentTextFinalFrame,
            TranscriptFinalFrame,
        )

        frame = TranscriptFinalFrame(
            type=StreamFrameType.TRANSCRIPT_FINAL,
            tenant_id=self.claims["tenant_id"],
            session_id=self.session_id,
            turn_id=f"turn-{uuid.uuid4().hex[:8]}",
            correlation_id=self.claims["correlation_id"],
            causation_id=None,
            seq=0,
            ts=_now(),
            text=text,
            asr_confidence=1.0,
        )

        started = time.perf_counter()
        async with contextlib.aclosing(self.agent.process(frame)) as frames:
            async for output in frames:
                if isinstance(output, AgentTextFinalFrame):
                    print(f"  agent> {output.text}")
        print(f"  ({time.perf_counter() - started:.2f}s)")

    async def _voice_turn(self, text: str) -> None:
        """Speak the line, recognise it, run the graph, speak the reply back."""
        import math

        from cordia_agent.streaming.pipeline import Pipeline, PipelineSession
        from cordia_agent.streaming.providers.audio_codec import PcmResampler
        from cordia_agent.streaming_models import (
            AgentTextFinalFrame,
            ToolProgressFrame,
            TranscriptFinalFrame,
        )

        await self._ensure_audio()
        settings = self._settings

        native = await self._synthesize(text, voice=CALLER_VOICE)
        caller_pcm = PcmResampler(
            source_rate_hz=settings.tts_sample_rate_hz,
            target_rate_hz=settings.input_sample_rate_hz,
        ).resample(native)

        chunk_bytes = (settings.input_sample_rate_hz * 2 * PACING_CHUNK_MS) // 1000
        seconds_per_chunk = chunk_bytes / (settings.input_sample_rate_hz * 2)
        tail_seconds = self.silence_ms / 1000 + 1.5

        async def audio():
            for offset in range(0, len(caller_pcm), chunk_bytes):
                yield caller_pcm[offset : offset + chunk_bytes]
                await asyncio.sleep(seconds_per_chunk)
            silence = b"\x00" * chunk_bytes
            for _ in range(math.ceil(tail_seconds / seconds_per_chunk)):
                yield silence
                await asyncio.sleep(seconds_per_chunk)

        pipeline = Pipeline(
            stt_provider=self._stt,
            tts_provider=self._tts,
            agent=self.agent,
            turn_manager=self._turn_manager,
        )
        session = PipelineSession(
            tenant_id=self.claims["tenant_id"],
            session_id=self.session_id,
            turn_id=f"turn-{uuid.uuid4().hex[:8]}",
            correlation_id=self.claims["correlation_id"],
        )

        started = time.perf_counter()
        progress = 0
        async with contextlib.aclosing(
            pipeline.run(audio(), session=session)
        ) as frames:
            async for frame in frames:
                if isinstance(frame, TranscriptFinalFrame):
                    # What the recogniser made of you — often the whole story.
                    print(f"  heard> {frame.text!r}")
                elif isinstance(frame, ToolProgressFrame):
                    progress += 1
                elif isinstance(frame, AgentTextFinalFrame):
                    print(f"  agent> {frame.text}")

        note = f", {progress} progress frame(s)" if progress else ""
        print(f"  ({time.perf_counter() - started:.2f}s{note})")

    async def _synthesize(self, text: str, *, voice: str) -> bytes:
        """Render one line to 24 kHz PCM through the speech route."""
        settings = self._settings
        response = await self._http.post(
            settings.speech_url(),
            headers=settings.auth_headers(),
            json={
                "model": settings.tts_deployment,
                "input": text,
                "voice": voice,
                "response_format": "pcm",
            },
        )
        if response.status_code >= 300:
            raise RuntimeError(f"speech synthesis failed: HTTP {response.status_code}")
        return response.content

    # -- commands ------------------------------------------------------------

    async def command(self, line: str) -> bool:
        """Handle a /command.  Returns False when the console should exit."""
        parts = line.split(maxsplit=1)
        name, rest = parts[0], (parts[1] if len(parts) > 1 else "")

        if name in ("/quit", "/exit", "/q"):
            return False

        if name == "/help":
            print(__doc__[__doc__.index("Commands:") :])
        elif name == "/voice":
            self.voice = True
            await self._ensure_audio()
            print("  (voice channel — your lines will be spoken and recognised)")
        elif name == "/text":
            self.voice = False
            print("  (text channel — transcripts go straight to the graph)")
        elif name == "/state":
            await self._show_state(rest.strip())
        elif name == "/audio":
            self._show_audio()
        elif name == "/reset":
            await self.open()
            print(f"  (new session {self.session_id})")
        else:
            print(f"  unknown command {name!r} — /help for the list")
        return True

    async def _show_state(self, key: str) -> None:
        """Print the checkpointed state, or one key of it."""
        values = (await self.graph.aget_state(self.agent.config)).values
        if key:
            print(f"  {key} = {_fmt(values.get(key), limit=2000)}")
            return
        for name in (
            "terminal_state",
            "operation_id",
            "submission_status",
            "availability_mode",
            "availability_checked",
            "disclosures_presented",
            "read_back_version",
            "pending_clarify_prompt",
        ):
            print(f"  {name:24s}= {_fmt(values.get(name), limit=160)}")
        draft = values.get("draft")
        if draft is not None:
            request = draft.request if hasattr(draft, "request") else draft["request"]
            print(
                f"  draft.request           = {_fmt(request.model_dump(mode='json'))}"
            )

    def _show_audio(self) -> None:
        """List the WAVs written for this session."""
        if self._media is None or not self._media.files:
            print("  (no audio written — take a turn on the voice channel first)")
            return
        for turn_id, path in self._media.files.items():
            print(f"  {turn_id:28s} {path}")


def _now():
    from datetime import UTC, datetime

    return datetime.now(UTC)


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


async def main() -> int:
    """Run the console until the user quits."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-base-url", default=None)
    parser.add_argument("--did", default=DEFAULT_DID)
    parser.add_argument(
        "--voice", action="store_true", help="Start on the voice channel."
    )
    parser.add_argument("--out-dir", default=".tmp/console")
    parser.add_argument("--silence-ms", type=int, default=900)
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
    # it must be set BEFORE cordia_agent.graph is imported.
    os.environ["CORDIA_API_BASE_URL"] = base_url

    from cordia_agent.slots.capture import get_slot_extractor
    from cordia_agent.tools.client import get_tool_client

    get_tool_client()  # raises if the CORDIA_API_BASE_URL bootstrap did not register
    get_slot_extractor()  # raises if the slot-extractor bootstrap did not register

    console = Console(
        base_url=base_url,
        did=args.did,
        out_dir=Path(args.out_dir),
        silence_ms=args.silence_ms,
    )
    await console.open()
    console.voice = args.voice
    if args.voice:
        await console._ensure_audio()

    print(
        BANNER.format(
            channel="voice" if console.voice else "text", session=console.session_id
        )
    )

    try:
        while True:
            try:
                # input() blocks; off-thread so the event loop stays free for
                # the audio pump and the graph.
                line = (await asyncio.to_thread(input, "you> ")).strip()
            except (EOFError, KeyboardInterrupt):
                print()
                break

            if not line:
                continue
            if line.startswith("/"):
                if not await console.command(line):
                    break
                continue

            try:
                await console.take_turn(line)
            except Exception as exc:  # keep the session alive across a bad turn
                print(f"  !! turn failed: {type(exc).__name__}: {exc}")
    finally:
        await console.close()

    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
