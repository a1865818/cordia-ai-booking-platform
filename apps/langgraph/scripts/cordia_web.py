"""Talk to Cordia in a browser: microphone in, agent speech out.

The console and the e2e runners both put text in front of the audio path — you
type a line and it gets synthesized for you. This does not: your microphone is
the caller, your speakers are the phone, and the round trip is the real one
(Azure realtime STT → the §8.3 graph → the .NET API → Postgres → gpt-4o-mini-tts
→ back to your ears).

Why a browser
    A browser is the shortest path to a microphone and a speaker that works on
    every machine without a native audio dependency, and it gives somewhere to
    SHOW the frames — partial transcripts, what was committed, which tools ran,
    what the agent decided — next to the audio. Half of what makes a voice bug
    diagnosable is seeing the transcript that caused it.

The media plane, again
    ``WebSocketMediaDelivery`` below is a ``MediaDelivery`` — the same two-method
    Protocol ``WavFileMediaPlane`` implements. Nothing else changes: the same
    ``TranscodingAudioSink``, provider, chunker and pipeline serve a browser and
    a WAV recorder, which is the property that makes swapping in Twilio Media
    Streams a small job rather than a rewrite.

Prerequisites are the handoff doc's (docker, the API on :8000, the seed applied,
apps/langgraph/.env). Run:

    cd apps/langgraph && uv run python scripts/cordia_web.py
    # then open http://localhost:8080

Microphone access needs a secure context; ``localhost`` counts, so no TLS is
required as long as you do not reach it over the network by IP.
"""

from __future__ import annotations

import argparse
import asyncio
import contextlib
import json
import logging
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


logger = logging.getLogger("cordia.web")

DEFAULT_API_BASE_URL = "http://localhost:8000"
DEFAULT_DID = "+61400000001"
#: Stand-in caller number for the browser demo.  A phone call brings its own ANI
#: and the telephony ingress passes it through as `from`; a browser tab has no
#: such number, so without one the booking would be created with no guest
#: attached and nowhere to send a confirmation.
DEFAULT_CALLER_E164 = "+61400000002"

#: Rate the browser captures at and the realtime socket expects.
CAPTURE_RATE_HZ = 16000

#: Voice used when a typed line is spoken back at the recogniser — different
#: from the agent's, so a recording of a session has two distinguishable sides.
CALLER_VOICE = "onyx"

#: Rate the agent's speech is delivered to the browser at.  24 kHz is the TTS
#: deployment's native output, so the default costs no conversion and no
#: quality; ``--egress pstn`` runs it through the 8 kHz mu-law path instead,
#: which is what you want when you are checking how the agent sounds on a phone.
BROWSER_RATE_HZ = 24000

#: Chunks retained for ref resolution.  A browser plays audio and drops it; the
#: refs only need to stay redeemable long enough to be useful for debugging.
MAX_RETAINED_CHUNKS = 256


def quieten_third_party_noise() -> None:
    """Silence warnings that would otherwise bury the server log.

    The OpenAI SDK declares its Responses ``output`` field as a wide union, so a
    structured-output call emits one pydantic serializer warning PER union
    member — dozens of lines per model call. Matched narrowly on the message so
    a genuine warning from our own models still gets through.
    """
    import warnings

    warnings.filterwarnings(
        "ignore", message=r"Pydantic serializer warnings", category=UserWarning
    )


class WebSocketMediaDelivery:
    """``MediaDelivery`` that streams converted audio to a connected browser.

    Implements exactly the Protocol ``WavFileMediaPlane`` does — ``deliver``
    returns an opaque locator, ``fetch`` redeems one — so
    ``TranscodingAudioSink`` cannot tell the two apart.
    """

    def __init__(self, send_bytes: Any) -> None:
        """Initialise with the socket's binary-send coroutine.

        Args:
            send_bytes: ``async (bytes) -> None``, the WebSocket's binary send.
        """
        self._send = send_bytes
        self._chunks: dict[str, bytes] = {}
        self._order: list[str] = []

    async def deliver(self, audio: bytes, metadata: Any, egress: Any) -> str:
        """Push one converted chunk to the browser and return its locator."""
        locator = f"{metadata.turn_id}/{metadata.chunk_seq}/{metadata.frame_seq}"
        self._chunks[locator] = audio
        self._order.append(locator)
        # Bounded: a long call would otherwise retain every byte it ever spoke.
        while len(self._order) > MAX_RETAINED_CHUNKS:
            self._chunks.pop(self._order.pop(0), None)
        await self._send(audio)
        return locator

    async def fetch(self, locator: str) -> bytes:
        """Return a delivered chunk, while it is still retained.

        Raises:
            KeyError: if the locator was never delivered, or has aged out.
        """
        try:
            return self._chunks[locator]
        except KeyError:
            raise KeyError(f"unknown or expired media locator: {locator!r}") from None


class BrowserSession:
    """One browser tab: one call, one graph thread, one pre-warmed STT session."""

    def __init__(
        self,
        *,
        base_url: str,
        did: str,
        egress: Any,
        silence_ms: int,
        caller: str = DEFAULT_CALLER_E164,
    ) -> None:
        self.base_url = base_url
        self.did = did
        self.egress = egress
        self.silence_ms = silence_ms
        self.caller = caller
        self.audio: asyncio.Queue[bytes | None] = asyncio.Queue()
        self._closed = False

    async def start(self, send_json: Any, send_bytes: Any) -> None:
        """Mint the tenant context and build the whole stack for this tab."""
        import httpx
        from langgraph.checkpoint.memory import InMemorySaver

        from cordia_agent.graph import build_agent_graph
        from cordia_agent.streaming.graph_agent_port import (
            AgentSessionContext,
            GraphAgentPort,
        )
        from cordia_agent.streaming.providers.audio_config import (
            audio_settings_from_env,
        )
        from cordia_agent.streaming.providers.azure_realtime_stt import (
            AzureRealtimeSTTProvider,
        )
        from cordia_agent.streaming.providers.azure_tts import AzureTtsProvider
        from cordia_agent.streaming.providers.media_plane import TranscodingAudioSink
        from cordia_agent.streaming.turn_manager import TurnManager

        self.send_json = send_json

        async with httpx.AsyncClient(base_url=self.base_url, timeout=15.0) as http:
            response = await http.post(
                "/api/channels/telephony",
                json={
                    "to": self.did,
                    "from": self.caller,
                    "caller_name": "Browser",
                },
            )
        if response.status_code != 200:
            raise RuntimeError(
                f"telephony webhook returned {response.status_code}. Is the API "
                "running, and has apps/api/scripts/seed-dev-tenant.sql been applied?"
            )
        minted = response.json()
        self.claims = minted["claims"]
        self.session_id = self.claims["session_id"]

        os.environ["CORDIA_AUDIO_REALTIME_SILENCE_DURATION_MS"] = str(self.silence_ms)
        os.environ["CORDIA_AUDIO_INPUT_SAMPLE_RATE_HZ"] = str(CAPTURE_RATE_HZ)
        self.settings = audio_settings_from_env()

        self.delivery = WebSocketMediaDelivery(send_bytes)
        sink = TranscodingAudioSink(delivery=self.delivery, egress=self.egress)
        self.http = httpx.AsyncClient(timeout=None)
        self.tts = AzureTtsProvider(self.settings, self.http, sink)
        self.stt = AzureRealtimeSTTProvider(self.settings)
        self.turn_manager = TurnManager()

        self.graph = build_agent_graph(checkpointer=InMemorySaver())
        self.agent = GraphAgentPort(
            self.graph,
            session=AgentSessionContext(
                signed_tenant_context=minted["tenant_context"],
                session_id=self.session_id,
                tenant_id=self.claims["tenant_id"],
                correlation_id=self.claims["correlation_id"],
                # Contact hint only: the tenant came from the signed context the
                # ingress minted off the called DID, never from this (Inv-13).
                caller_phone_e164=self.caller,
                channel="voice",
                language="en-AU",
            ),
        )

        started = time.perf_counter()
        await self.stt.prewarm()
        await send_json(
            {
                "type": "ready",
                "session_id": self.session_id,
                "tenant_id": self.claims["tenant_id"],
                "prewarm_seconds": round(time.perf_counter() - started, 2),
                "capture_rate_hz": CAPTURE_RATE_HZ,
                "playback_rate_hz": self.egress.sample_rate_hz,
                "egress": f"{self.egress.sample_rate_hz} Hz {self.egress.encoding}",
            }
        )

    async def speak_as_caller(self, text: str) -> None:
        """Synthesize *text* in the caller's voice and push it at the recogniser.

        Typing a line is a convenience, NOT a bypass: the audio still goes
        through the real STT socket, so what the graph receives is what a
        recogniser produced from speech — which is frequently not what was
        typed, and is exactly the difference worth being able to see.
        """
        from cordia_agent.streaming.providers.audio_codec import PcmResampler

        response = await self.http.post(
            self.settings.speech_url(),
            headers=self.settings.auth_headers(),
            json={
                "model": self.settings.tts_deployment,
                "input": text,
                "voice": CALLER_VOICE,
                "response_format": "pcm",
            },
        )
        if response.status_code >= 300:
            await self.send_json(
                {
                    "type": "error",
                    "detail": f"caller synthesis failed: HTTP {response.status_code}",
                }
            )
            return

        pcm = PcmResampler(
            source_rate_hz=self.settings.tts_sample_rate_hz,
            target_rate_hz=CAPTURE_RATE_HZ,
        ).resample(response.content)

        # Delivered in the same 100 ms packets a microphone produces, and paced
        # to wall-clock: the service's VAD endpoints on real time, so a burst
        # would read as the caller pausing mid-sentence.
        chunk = (CAPTURE_RATE_HZ * 2 * 100) // 1000
        for offset in range(0, len(pcm), chunk):
            await self.audio.put(pcm[offset : offset + chunk])
            await asyncio.sleep(0.1)
        silence = b"\x00" * chunk
        for _ in range(int((self.silence_ms / 1000 + 1.0) / 0.1)):
            await self.audio.put(silence)
            await asyncio.sleep(0.1)

    async def close(self) -> None:
        """Release the STT session and HTTP client."""
        self._closed = True
        await self.audio.put(None)
        with contextlib.suppress(Exception):
            await self.stt.aclose()
        with contextlib.suppress(Exception):
            await self.http.aclose()

    # -- the turn loop -------------------------------------------------------

    async def run_turns(self) -> None:
        """Run turns back to back for as long as the tab is open.

        ``Pipeline.run`` covers exactly one turn — it ends when STT commits a
        final and the agent has finished answering it. A call is therefore a
        loop of pipelines over ONE shared microphone queue: audio that arrives
        while the agent is talking stays queued and becomes the next turn.
        """
        from cordia_agent.streaming.pipeline import Pipeline, PipelineSession

        while not self._closed:
            pipeline = Pipeline(
                stt_provider=self.stt,
                tts_provider=self.tts,
                agent=self.agent,
                turn_manager=self.turn_manager,
            )
            session = PipelineSession(
                tenant_id=self.claims["tenant_id"],
                session_id=self.session_id,
                turn_id=f"turn-{uuid.uuid4().hex[:8]}",
                correlation_id=self.claims["correlation_id"],
            )
            try:
                await self._run_one_turn(pipeline, session)
            except Exception as exc:
                logger.exception("turn failed")
                await self.send_json(
                    {"type": "error", "detail": f"{type(exc).__name__}: {exc}"}
                )
                # Keep the line open: one failed turn should not end the call.
                await asyncio.sleep(0.5)

    async def _run_one_turn(self, pipeline: Any, session: Any) -> None:
        """Drive one pipeline turn, forwarding every frame to the browser."""
        from cordia_agent.streaming_models import (
            AgentTextFinalFrame,
            BargeInFrame,
            ToolProgressFrame,
            TranscriptFinalFrame,
            TranscriptPartialFrame,
        )

        started = time.perf_counter()

        # One 100 ms packet of silence, in the capture format.
        comfort_noise = b"\x00" * ((CAPTURE_RATE_HZ * 2 * 100) // 1000)

        async def microphone():
            """Yield mic packets, filling gaps with silence.

            A real line always carries audio; a browser using push-to-talk does
            not, and the moment the queue runs dry the service stops receiving
            anything at all. Its server-side VAD then has no trailing silence to
            endpoint on, so the utterance never closes and the turn dies on the
            30-second watchdog instead of on a transcript. Feeding comfort noise
            makes a released push-to-talk button look like what it actually is:
            the caller having stopped speaking.
            """
            while True:
                try:
                    chunk = await asyncio.wait_for(self.audio.get(), timeout=0.1)
                except TimeoutError:
                    yield comfort_noise
                    continue
                if chunk is None:
                    return
                yield chunk

        async with contextlib.aclosing(
            pipeline.run(microphone(), session=session)
        ) as frames:
            async for frame in frames:
                elapsed = round(time.perf_counter() - started, 2)
                if isinstance(frame, TranscriptPartialFrame):
                    await self.send_json(
                        {
                            "type": "partial",
                            "text": frame.text,
                            "onset": not frame.text,
                            "at": elapsed,
                        }
                    )
                elif isinstance(frame, TranscriptFinalFrame):
                    await self.send_json(
                        {
                            "type": "heard",
                            "text": frame.text,
                            "asr_confidence": frame.asr_confidence,
                            "at": elapsed,
                        }
                    )
                elif isinstance(frame, ToolProgressFrame):
                    await self.send_json(
                        {
                            "type": "progress",
                            "tool": frame.tool_name,
                            "elapsed_ms": frame.elapsed_ms,
                            "at": elapsed,
                        }
                    )
                elif isinstance(frame, BargeInFrame):
                    await self.send_json({"type": "barge_in", "at": elapsed})
                elif isinstance(frame, AgentTextFinalFrame):
                    await self.send_json(
                        {"type": "said", "text": frame.text, "at": elapsed}
                    )

        # Inv-1 gives a turn exactly one committable final, so whatever is still
        # queued here is the TAIL of the utterance just committed — the part the
        # server-side VAD cut off at an internal pause. Carrying it into the next
        # turn is how "…at location loc_001, for four people" becomes a garbage
        # standalone transcript. Dropping it is the honest behaviour for this
        # harness; a caller who genuinely talked over the agent re-presses and
        # says it again.
        dropped = 0
        while not self.audio.empty():
            with contextlib.suppress(asyncio.QueueEmpty):
                self.audio.get_nowait()
                dropped += 1
        if dropped:
            logger.debug("dropped %d queued packet(s) from the committed turn", dropped)

        await self._send_state()

    async def _send_state(self) -> None:
        """Push the interesting bits of graph state after a turn."""
        values = (await self.graph.aget_state(self.agent.config)).values
        draft = values.get("draft")
        request = None
        if draft is not None:
            model = draft.request if hasattr(draft, "request") else draft["request"]
            request = model.model_dump(mode="json")

        await self.send_json(
            {
                "type": "state",
                "state": {
                    "terminal_state": values.get("terminal_state"),
                    "operation_id": values.get("operation_id"),
                    "submission_status": values.get("submission_status"),
                    "availability_mode": values.get("availability_mode"),
                    "disclosures_presented": values.get("disclosures_presented"),
                    "pending_clarify_prompt": values.get("pending_clarify_prompt"),
                    "draft": request,
                },
            }
        )


def build_app(
    *,
    base_url: str,
    did: str,
    egress: Any,
    silence_ms: int,
    caller: str = DEFAULT_CALLER_E164,
) -> Any:
    """Build the Starlette app serving the page and the audio socket."""
    from starlette.applications import Starlette
    from starlette.responses import HTMLResponse
    from starlette.routing import Route, WebSocketRoute

    page = (Path(__file__).parent / "cordia_web.html").read_text()

    async def index(request: Any) -> Any:
        return HTMLResponse(page)

    async def socket(websocket: Any) -> None:
        await websocket.accept()
        session = BrowserSession(
            base_url=base_url,
            did=did,
            egress=egress,
            silence_ms=silence_ms,
            caller=caller,
        )

        async def send_json(payload: dict) -> None:
            with contextlib.suppress(Exception):
                await websocket.send_text(json.dumps(payload))

        async def send_bytes(data: bytes) -> None:
            with contextlib.suppress(Exception):
                await websocket.send_bytes(data)

        try:
            await session.start(send_json, send_bytes)
        except Exception as exc:
            logger.exception("session start failed")
            await send_json({"type": "error", "detail": str(exc)})
            await websocket.close()
            return

        turns = asyncio.create_task(session.run_turns(), name="cordia-web-turns")
        try:
            while True:
                message = await websocket.receive()
                if message["type"] == "websocket.disconnect":
                    break
                if (data := message.get("bytes")) is not None:
                    await session.audio.put(data)
                elif (text := message.get("text")) is not None:
                    payload = json.loads(text)
                    if payload.get("type") == "say" and payload.get("text"):
                        # Off-task: speaking the line paces itself to wall-clock,
                        # and blocking the receive loop would stall the mic.
                        asyncio.create_task(  # noqa: RUF006
                            session.speak_as_caller(payload["text"])
                        )
        except Exception:
            logger.debug("browser socket closed")
        finally:
            turns.cancel()
            await asyncio.gather(turns, return_exceptions=True)
            await session.close()

    return Starlette(
        routes=[Route("/", index), WebSocketRoute("/ws", socket)],
    )


def main() -> int:
    """Serve the browser interface until interrupted."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-base-url", default=None)
    parser.add_argument("--did", default=DEFAULT_DID)
    parser.add_argument(
        "--caller",
        default=DEFAULT_CALLER_E164,
        help=(
            "E.164 number to present as the caller. A real phone call carries "
            "an ANI and a browser tab does not, so the demo has to supply one: "
            "without it the booking is created with no guest attached and no "
            "destination to notify. It is a contact hint and nothing more - "
            "the tenant is resolved from the called DID alone (Inv-13)."
        ),
    )
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument(
        "--egress",
        choices=("browser", "pstn"),
        default="browser",
        help=(
            "browser: 24 kHz linear PCM, the TTS deployment's native output. "
            "pstn: 8 kHz G.711 mu-law, so you hear what a phone caller hears."
        ),
    )
    parser.add_argument(
        "--silence-ms",
        type=int,
        default=900,
        help=(
            "Trailing silence the server-side VAD needs before it endpoints. "
            "Higher than the library default of 500: a turn commits ONE final, "
            "so a sentence split at an internal pause loses its tail."
        ),
    )
    parser.add_argument(
        "--log-level",
        default="warning",
        choices=("debug", "info", "warning", "error"),
        help="debug shows the realtime socket's frame-by-frame behaviour.",
    )
    args = parser.parse_args()

    quieten_third_party_noise()
    logging.basicConfig(
        level=getattr(logging, args.log_level.upper()),
        format="%(levelname)s %(name)s %(message)s",
    )
    for noisy in ("httpx", "httpcore", "websockets.client"):
        logging.getLogger(noisy).setLevel(logging.WARNING)

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
    from cordia_agent.streaming.providers.media_plane import AudioEgress
    from cordia_agent.tools.client import get_tool_client

    get_tool_client()  # raises if the CORDIA_API_BASE_URL bootstrap did not register
    get_slot_extractor()  # raises if the slot-extractor bootstrap did not register

    egress = (
        AudioEgress.pstn()
        if args.egress == "pstn"
        else AudioEgress(sample_rate_hz=BROWSER_RATE_HZ, encoding="pcm16")
    )

    import uvicorn

    app = build_app(
        base_url=base_url,
        did=args.did,
        egress=egress,
        silence_ms=args.silence_ms,
        caller=args.caller,
    )

    print(f"API base URL: {base_url}")
    print(f"Agent audio:  {egress.sample_rate_hz} Hz {egress.encoding}")
    print(f"\n  open http://{args.host}:{args.port}\n")

    uvicorn.run(app, host=args.host, port=args.port, log_level="warning")
    return 0


if __name__ == "__main__":
    sys.exit(main())
