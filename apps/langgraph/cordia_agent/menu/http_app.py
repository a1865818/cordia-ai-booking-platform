"""In-process HTTP surface for the isolated menu-extraction agent (spec 11 §3).

``langgraph.json`` mounts this module's :data:`app` through its ``http.app``
key, so ``POST /internal/menu/extract`` is served by the SAME process that runs
the graph — no second server, no second deployment unit.

Why a dedicated route and not a registered graph
    Extraction is a stateless RPC: text in, ``items`` + ``flags`` out.  There is
    no thread, no checkpoint, no interrupt and no tool loop, so the Run API
    would drag in surface (``/runs/stream``, thread state, an open-shaped output
    envelope) that this contract must not expose.  A graph's response envelope
    is its whole output state — arbitrary by construction — whereas this route
    returns exactly :class:`~cordia_agent.menu.extraction_agent.ExtractionResult`
    (``extra="forbid"``: ``items`` + ``flags``, nothing else), a wire shape that
    *structurally cannot* express a privileged action (Inv-12 / §27.1).  Error
    semantics settle it: ``/runs/wait`` can answer ``200`` with an error inside
    the payload, while the .NET caller needs failures to be real 4xx/5xx so its
    fail-secure catch settles ``ExtractFailed``.

Fail closed, never fail open
    A model failure NEVER becomes ``200 {"items": []}``.  An empty item list is
    a legitimate extraction outcome (a blank document), so answering ``200``
    with one on error would settle the ingestion as a successful — but empty —
    draft catalog.  Every failure category maps to a distinct status:
    ``503`` for :exc:`~cordia_agent.llm.config.ModelNotConfigured` (an operator
    problem), ``502`` for
    :exc:`~cordia_agent.menu.model_azure.MenuExtractionModelError` (a model
    problem), ``500`` for anything unforeseen.

    The same rule now covers AUTHENTICATION, which used to be the one place this
    module failed open: an unset ``CORDIA_MENU_EXTRACTION_API_KEY`` meant "no
    check at all", and that is exactly the state the deployed image shipped in
    (issue #94).  No key configured is a ``503``, not a green light, unless
    ``CORDIA_MENU_EXTRACTION_ALLOW_ANONYMOUS`` says so in as many words — see
    :func:`_auth_rejection`.

Untrusted content never leaves through an error path
    The menu document is untrusted input (§8.11 / Inv-12) and provider errors
    routinely quote the request back.  Three rules follow, and all three are
    load-bearing:

    1. Error bodies are ``{"error": "<category>"}`` and nothing else — never an
       exception message, never a validation report (pydantic quotes the
       offending input), never the document.
    2. Nothing here calls ``logger.exception`` or hands an exception OBJECT to a
       reporter.  ``model_azure`` raises ``from None``, which clears
       ``__cause__`` and sets ``__suppress_context__`` — honoured by
       ``traceback`` and ``logging``, but ``__context__`` still references the
       provider exception, so a reporter that walks it itself would see the
       provider message.  Only the exception TYPE NAME is ever logged.
    3. No unhandled exception may reach Starlette's default 500 handler, which
       would format one.  :func:`extract_menu` therefore wraps the whole request
       in a catch-all.

    ``artifact_ref`` gets the same treatment: it is accepted for log correlation
    ONLY — never fetched, never interpreted — and even then it is logged as a
    scheme plus a digest, because ``inline:<the entire menu>`` is a legal value
    the .NET caller sends today.

Joinable, without trusting the caller to be joinable safely
    ``artifact_ref`` alone does not make this lane diagnosable: the .NET client
    drops it above 256 characters, which is every real inline menu, so the
    artifact token is ``none`` in practice.  Every log line here therefore also
    carries ``correlation=``, taken from the ``X-Correlation-Id`` header the
    client sends from its ambient correlation context — the one field that joins
    a ``502`` on its side to the reason on this one.

    That value is caller-supplied, so it is filtered rather than trusted: bounded
    in length and restricted to a log-safe character set, else ``opaque`` (see
    :func:`_correlation_log_token`).  An unfiltered header would let an
    unauthenticated caller on ``:2024`` forge whole log records with a newline,
    or repaint an operator's terminal with an ANSI escape.

Bounded before buffered
    ``:2024`` is an unauthenticated port in dev, so an unbounded
    ``await request.body()`` would be a trivial memory DoS.  The body is read as
    a stream against a byte budget and abandoned the moment it exceeds it, and
    a declared ``Content-Length`` over the budget is rejected before a single
    chunk is read.  Both the auth gate and the media-type gate sit AHEAD of that
    read, so a caller this route is going to reject never gets it to pull a byte
    off the socket.

    The media-type gate earns its place for a second reason: ``text/plain``,
    the form encodings and a missing ``Content-Type`` are CORS "simple
    requests", so without it a hostile page a developer merely visits could
    quietly POST at ``localhost:2024`` and burn provider generations.
    Demanding ``application/json`` forces a preflight it cannot satisfy.
"""

from __future__ import annotations

import hashlib
import hmac
import logging
import os
from contextvars import ContextVar
from typing import TYPE_CHECKING, Any, Protocol

from pydantic import BaseModel, Field, ValidationError
from starlette.applications import Starlette
from starlette.responses import JSONResponse
from starlette.routing import Route

from cordia_agent.llm.config import ModelNotConfigured
from cordia_agent.menu.extraction_agent import (
    ExtractionFlag,
    ExtractionResult,
    IsolatedExtractionAgent,
)
from cordia_agent.menu.model_azure import MenuExtractionModelError


if TYPE_CHECKING:
    from starlette.requests import Request

    from cordia_agent.contracts.catalog import CatalogItem
    from cordia_agent.menu.model_azure import ExtractionOutcome
    from cordia_agent.safety.injection_harness import LabeledContent


_log = logging.getLogger(__name__)

#: The one route this app serves.  Mirrored by the .NET client's
#: ``LangGraph:ExtractPath`` default — change both or neither.
EXTRACT_PATH = "/internal/menu/extract"

#: Shared secret guarding the route (D-G).  Set ⇒ every request must carry a
#: matching :data:`INTERNAL_KEY_HEADER`.  Unset or blank ⇒ the route refuses to
#: serve at all (``503``) unless :data:`ALLOW_ANONYMOUS_ENV_VAR` is explicitly
#: opted into.  Read per request, never at import time, so a redeploy that adds
#: the secret does not need the module reloaded.
API_KEY_ENV_VAR = "CORDIA_MENU_EXTRACTION_API_KEY"

#: Explicit opt-out from authentication, for the local loop only.
#:
#: The route used to FAIL OPEN when :data:`API_KEY_ENV_VAR` was unset, and that
#: is exactly the state the deployed image shipped in: the key was set nowhere,
#: LangGraph Platform auth does not cover custom routes unless
#: ``enable_custom_route_auth`` is on (it is not), and the route is published in
#: the deployed ``/openapi.json`` and ``/docs`` (issue #94).  "No key configured"
#: and "no authentication wanted" are different statements, and only the second
#: one is safe to infer from a missing environment variable if it was TYPED.
#:
#: So the default is now closed and dev opts out by name.  ``langgraph dev``
#: binds an unauthenticated port on localhost, where a shared secret buys
#: nothing, and requiring one would have made the local loop harder than the
#: deployment.
#:
#: Any value other than the empty string counts as opted out — this is a
#: developer's own switch, not a tuning knob, and a deployment that types it at
#: all has said something unambiguous.  It is IGNORED when a key is configured:
#: a key present means the check runs, full stop, so a stale opt-out in a
#: deployment's environment cannot silently disarm it.
ALLOW_ANONYMOUS_ENV_VAR = "CORDIA_MENU_EXTRACTION_ALLOW_ANONYMOUS"

#: Header the .NET caller puts the shared secret in.  Never logged.
INTERNAL_KEY_HEADER = "X-Cordia-Internal-Key"

#: Header carrying the caller's ambient correlation id (minted once at ingress,
#: never re-minted mid-flow).  It is the ONLY thing that joins a log line here to
#: the .NET client's own: ``artifact_ref`` is dropped by the client above 256
#: characters, which is every real inline menu, so ``artifact=none`` is the norm.
#: Logged on EVERY outcome of this route, sanitized — see
#: :func:`_correlation_log_token`.
CORRELATION_ID_HEADER = "X-Correlation-Id"

#: Longest correlation id written to a log line verbatim.  Comfortably clears the
#: real shapes (a GUID, or the ``cat_<32 hex>`` catalog id the lane falls back
#: to) while denying an unauthenticated caller a kilobyte of log line per
#: request.
MAX_CORRELATION_ID_CHARS = 64

#: Non-alphanumeric characters a correlation id may contain and still be logged
#: verbatim.  ``-`` covers a dashed GUID; ``_`` is NOT optional — the .NET lane
#: falls back to the catalog id, ``cat_<guid:N>``, so omitting it would reduce
#: essentially every real ingestion to ``correlation=opaque`` and re-open exactly
#: the gap this token exists to close.
_CORRELATION_EXTRA_CHARS = frozenset("-_")

#: Ceiling on the menu document itself, matching the .NET-side artifact
#: resolver's cap (D-B) so the two boundaries agree on what "too big" means.
MAX_MENU_TEXT_BYTES = 256 * 1024

#: Ceiling on the raw REQUEST BODY, which is a JSON envelope around the document
#: and therefore legitimately larger than it.  The multiplier is FOUR because
#: the worst case is measured, not guessed: the .NET caller serializes with
#: ``System.Text.Json``'s default ``JavaScriptEncoder``, which escapes EVERY
#: non-ASCII character to ``\uXXXX``.  Serializing a document that sits exactly
#: on :data:`MAX_MENU_TEXT_BYTES` through the client's own options gives these
#: body-to-text ratios:
#:
#: - ASCII: 1.0x — only the document's own quotes and newlines are escaped.
#: - CJK (3 UTF-8 bytes/char): 2.0x — 3 bytes in, 6 out.
#: - accented Latin (2 bytes/char): 3.0x — 2 bytes in, 6 out.
#: - emoji (4 bytes, astral): 3.0x — 4 bytes in, 12 out, a surrogate PAIR
#:   escaped as two ``\uXXXX`` sequences.
#:
#: At ``2 *`` the cap, every Vietnamese, Czech, Polish or emoji-bearing menu was
#: rejected with a ``413`` at roughly a THIRD of the documented 256 KiB, and the
#: owner saw only ``ExtractFailed`` with no explanation.  ``4 *`` clears the real
#: 3x worst case with envelope room left over and still bounds what an
#: unauthenticated caller can make this process buffer.
#:
#: Configuring the client to emit relaxed UTF-8 is NOT the alternative fix:
#: measured, both ``UnsafeRelaxedJsonEscaping`` and
#: ``JavaScriptEncoder.Create(UnicodeRanges.All)`` still emit non-BMP characters
#: as surrogate escapes, so emoji-heavy menus would keep failing while looking
#: fixed.
#:
#: This is a transport guard, NOT the contract — :data:`MAX_MENU_TEXT_BYTES` is
#: the authoritative cap and is still applied to the DECODED text, so nothing a
#: larger envelope admits can exceed the agreed document size.
MAX_REQUEST_BODY_BYTES = 4 * MAX_MENU_TEXT_BYTES

#: Document-level flag reason raised when the model returned rows that could not
#: be mapped.  Partial row loss is otherwise invisible: the port returns a bare
#: item list, so "30 items extracted" reads identically whether the document had
#: 30 rows or 40.  The owner review queue must be able to tell those apart.
ROWS_DROPPED_FLAG = "rows_dropped"

#: Per-request channel carrying :class:`ExtractionOutcome` from the model back
#: to this route.
#:
#: The route MUST go through
#: :class:`~cordia_agent.menu.extraction_agent.IsolatedExtractionAgent` — that
#: is where the empty ``ToolAllowlist`` and the ``PromptChannels`` untrusted-only
#: assembly live — but the agent calls the port's plain ``extract()``, which
#: returns only ``list[CatalogItem]``.  A context variable threads the drop
#: counts past it without widening the
#: :class:`~cordia_agent.menu.model_port.ExtractionModel` protocol, without
#: touching the agent, and without instance state on the model (which would race
#: the moment two requests shared one model — see ``ExtractionOutcome``'s own
#: docstring).  ``ContextVar`` is per-task, and each Starlette request runs in
#: its own task, so one request cannot read another's value; the handler also
#: clears it before every extraction so a copied context cannot carry a stale
#: one in.
_extraction_outcome: ContextVar[ExtractionOutcome | None] = ContextVar(
    "cordia_menu_extraction_outcome", default=None
)


class DetailedExtractionModel(Protocol):
    """An extraction model that also reports what one call silently dropped."""

    async def extract_detailed(self, content: LabeledContent) -> ExtractionOutcome:
        """Return the extracted items plus the per-call drop counts."""
        ...


class DropCountingModel:
    """``ExtractionModel`` decorator that publishes per-call drop counts.

    Satisfies :class:`~cordia_agent.menu.model_port.ExtractionModel`
    structurally, so
    :class:`~cordia_agent.menu.extraction_agent.IsolatedExtractionAgent` accepts
    it unchanged and every isolation property of the agent is preserved: this
    decorator adds no tool, no state and no side effect beyond publishing an
    integer pair to :data:`_extraction_outcome`.

    It holds no per-request state of its own, so one instance may back
    concurrent requests.
    """

    def __init__(self, inner: DetailedExtractionModel) -> None:
        """Wrap a model that implements ``extract_detailed``.

        Args:
            inner: The real extraction model, e.g.
                :class:`~cordia_agent.menu.model_azure.AzureExtractionModel`.
        """
        self._inner = inner

    async def extract(self, content: LabeledContent) -> list[CatalogItem]:
        """Extract items, publishing the call's drop counts on the way out.

        Args:
            content: The menu document text, labeled ``TrustLevel.UNTRUSTED``.

        Returns:
            The draft catalog items that survived mapping.

        Raises:
            cordia_agent.menu.model_azure.MenuExtractionModelError: propagated
                from the wrapped model.
            cordia_agent.llm.config.ModelNotConfigured: propagated from the
                wrapped model.
        """
        outcome = await self._inner.extract_detailed(content)
        _extraction_outcome.set(outcome)
        return outcome.items


class ExtractRequest(BaseModel):
    """The one accepted request shape for ``POST /internal/menu/extract``.

    ``extra="forbid"`` is the security property, not a nicety: a tool-shaped
    payload (``{"menu_text": ..., "tool_calls": [...]}``) is REJECTED rather
    than silently ignored, so no field a caller invents can ever be read by
    anything downstream.
    """

    model_config = {"extra": "forbid"}

    menu_text: str = Field(
        description="The menu document text. Untrusted data, never instructions."
    )
    artifact_ref: str | None = Field(
        default=None,
        description=(
            "Opaque reference to the artifact the text came from, for log "
            "correlation ONLY. This service never fetches it and never "
            "interprets it."
        ),
    )


# ---------------------------------------------------------------------------
# Module-level agent registry (mirrors cordia_agent.tools.client's pattern)
# ---------------------------------------------------------------------------

_extraction_agent: IsolatedExtractionAgent | None = None


def set_extraction_agent(agent: IsolatedExtractionAgent) -> None:
    """Register the process-wide extraction agent.

    Args:
        agent: The agent every request is served by until it is replaced or
            reset.  Tests inject one wrapping an offline fake model; nothing in
            ``cordia_agent`` ever does.
    """
    global _extraction_agent
    _extraction_agent = agent


def get_extraction_agent() -> IsolatedExtractionAgent:
    """Return the registered agent, constructing the production one on demand.

    Unlike :func:`~cordia_agent.tools.client.get_tool_client`, this does NOT
    fail closed when nothing is registered: the production wiring has no
    bootstrap hook to run (``langgraph.json`` imports this module and nothing
    else), so the default is built here on the first request.  Building it is
    still free and offline — :class:`AzureExtractionModel` resolves its chat
    model, and therefore the Azure environment, lazily on first use — which is
    what keeps importing this module cheap.

    Returns:
        The process-wide :class:`IsolatedExtractionAgent`.
    """
    global _extraction_agent
    if _extraction_agent is None:
        # Imported here purely to keep the CONSTRUCTION lazy — `model_azure`
        # itself is already in the import graph (MenuExtractionModelError is
        # imported at module scope), and it is that module's own laziness, not
        # this import site, that keeps the Azure SDK out of it.
        from cordia_agent.menu.model_azure import AzureExtractionModel

        _extraction_agent = IsolatedExtractionAgent(
            DropCountingModel(AzureExtractionModel())
        )
    return _extraction_agent


def reset_extraction_agent() -> None:
    """Clear the registered agent (test teardown helper)."""
    global _extraction_agent
    _extraction_agent = None


# ---------------------------------------------------------------------------
# Route
# ---------------------------------------------------------------------------


def _error(category: str, status_code: int) -> JSONResponse:
    """Build the ONLY error body this service ever returns.

    Args:
        category: A short, static, caller-independent failure category.  Never
            derived from the request, the document, or an exception message.
        status_code: The HTTP status to answer with.

    Returns:
        A ``{"error": "<category>"}`` JSON response.
    """
    return JSONResponse({"error": category}, status_code=status_code)


def _auth_rejection(request: Request, correlation: str) -> JSONResponse | None:
    """Return the response that ends this request, or ``None`` to proceed (D-G).

    Three outcomes, and the middle one is the point of this function:

    * a secret is configured and the request carries it — proceed;
    * a secret is configured and the request does not match — ``401``, compared
      with :func:`hmac.compare_digest` so the comparison cannot be timed to
      recover the secret byte by byte;
    * NO secret is configured — ``503`` unless
      :data:`ALLOW_ANONYMOUS_ENV_VAR` is set, because "nobody configured a key"
      must not silently mean "this route is public".  See that constant for why
      the default flipped.

    **Per request, not at import time.**  Raising at import would break the
    offline test suite and every local ``langgraph dev`` loop, and it would take
    the *graph* down with it — ``langgraph.json`` mounts this module into the
    same process, so an import-time failure over a route nobody has called yet
    would stop the booking lane too.  A per-request status keeps the failure
    scoped to the thing that is actually misconfigured, and ``503`` is the honest
    code: the route exists and is refusing to serve, which is also what the
    module already answers for an unconfigured model.

    Args:
        request: The inbound request.
        correlation: The sanitized correlation token, so a refusal here joins the
            .NET client's own line for the same ingestion.

    Returns:
        ``None`` when the request may proceed, else the response to return.
    """
    expected = os.environ.get(API_KEY_ENV_VAR, "").strip()
    if not expected:
        if os.environ.get(ALLOW_ANONYMOUS_ENV_VAR, ""):
            return None
        _log.error(
            "menu extraction is refusing every request: no %s is configured and "
            "%s was not set (correlation=%s)",
            API_KEY_ENV_VAR,
            ALLOW_ANONYMOUS_ENV_VAR,
            correlation,
        )
        return _error("auth_not_configured", 503)

    supplied = request.headers.get(INTERNAL_KEY_HEADER, "")
    # ASCII-only secrets, by contract (see .env.example).  Starlette decodes
    # header values as latin-1 while the environment arrives as UTF-8, so a
    # non-ASCII secret would compare unequal for a UTF-8 client and equal for a
    # latin-1 one — a silent interop split.  The two encodings agree exactly on
    # ASCII, which is all a shared secret needs to be.  The .NET side refuses to
    # START on a non-ASCII secret for the same reason.
    if hmac.compare_digest(supplied.encode("utf-8"), expected.encode("utf-8")):
        return None

    _log.warning(
        "menu extraction request rejected: bad or missing internal key "
        "(correlation=%s)",
        correlation,
    )
    return _error("unauthorized", 401)


def _is_json_request(request: Request) -> bool:
    """Return whether *request* declares a JSON body.

    Not pedantry — it is a CSRF/drive-by guard.  ``text/plain``, form encodings
    and a missing ``Content-Type`` are all CORS "simple requests", so without
    this check a hostile page a developer merely visits could POST 256 KiB at
    ``localhost:2024`` and burn provider generations.  Requiring
    ``application/json`` forces a preflight, which that page cannot satisfy (it
    also cannot send :data:`INTERNAL_KEY_HEADER`).  The response is unreadable
    cross-origin either way, so the exposure is cost, not exfiltration.

    Args:
        request: The inbound request.

    Returns:
        ``True`` when the media type is ``application/json``, parameters (e.g.
        ``; charset=utf-8``) allowed.
    """
    media_type = request.headers.get("content-type", "").split(";", 1)[0]
    return media_type.strip().lower() == "application/json"


async def _read_bounded_body(request: Request) -> bytes | None:
    """Buffer the request body, giving up if it exceeds the byte budget.

    The declared ``Content-Length`` is checked first so an honest oversized
    request costs nothing, and the streamed read is guarded independently so a
    chunked (or lying) one cannot slip past it.  A body that breaches the budget
    is abandoned mid-stream rather than being read to completion.

    Args:
        request: The inbound request.

    Returns:
        The complete body, or ``None`` when it exceeds
        :data:`MAX_REQUEST_BODY_BYTES`.
    """
    declared = request.headers.get("content-length")
    if (
        declared is not None
        and declared.isdigit()
        and (int(declared) > MAX_REQUEST_BODY_BYTES)
    ):
        return None

    chunks: list[bytes] = []
    total = 0
    async for chunk in request.stream():
        total += len(chunk)
        if total > MAX_REQUEST_BODY_BYTES:
            return None
        chunks.append(chunk)
    return b"".join(chunks)


def _artifact_log_token(artifact_ref: str | None) -> str:
    """Reduce *artifact_ref* to something safe to write to a log line.

    ``artifact_ref`` is caller-supplied and today's canonical value is
    ``inline:<the entire menu document>`` — logging it raw would put an
    untrusted document into a log aggregator (Inv-12/16), which is precisely
    what every other rule in this module exists to prevent.  What survives is a
    bounded scheme plus a digest: enough to correlate two log lines about the
    same artifact, useless as an exfiltration channel.

    Args:
        artifact_ref: The reference the caller sent, if any.

    Returns:
        A short, content-free correlation token.
    """
    if artifact_ref is None:
        return "none"
    scheme, separator, _ = artifact_ref.partition(":")
    safe_scheme = (
        scheme
        if separator and len(scheme) <= 16 and scheme.isascii() and scheme.isalnum()
        else "opaque"
    )
    digest = hashlib.sha256(artifact_ref.encode("utf-8")).hexdigest()[:12]
    return f"{safe_scheme}:{digest}"


def _correlation_log_token(request: Request) -> str:
    """Reduce the :data:`CORRELATION_ID_HEADER` to something safe to log.

    Unlike :func:`_artifact_log_token` this keeps the value ITSELF rather than a
    digest — a digest would be useless, since the whole point is to match the id
    the .NET client already logged on its own side.  That makes it
    caller-supplied text on its way into a log aggregator, so it is filtered
    rather than trusted: over-long, non-ASCII, or carrying anything outside
    ``[A-Za-z0-9]`` plus :data:`_CORRELATION_EXTRA_CHARS` and it becomes
    ``"opaque"``.  A newline would otherwise forge a whole log record and an ANSI
    escape could rewrite an operator's terminal.

    Absent (or blank) reads as ``"none"``, never as an empty field: a log line
    ending in ``correlation=`` is indistinguishable from a truncated one, and
    "the caller sent no id" is itself the diagnosis when the .NET side believed
    it had one.

    Args:
        request: The inbound request.

    Returns:
        The correlation id verbatim, ``"none"`` when the header is absent or
        blank, or ``"opaque"`` when the value is not safe to log.
    """
    raw = request.headers.get(CORRELATION_ID_HEADER)
    if raw is None:
        return "none"
    candidate = raw.strip()
    if not candidate:
        return "none"
    if len(candidate) > MAX_CORRELATION_ID_CHARS:
        return "opaque"
    # `str.isalnum` is True for non-ASCII letters and digits too (Starlette
    # decodes header bytes as latin-1, so any high byte arrives as one), hence
    # the explicit `isascii` guard rather than `isalnum` alone.
    if not all(
        char.isascii() and (char.isalnum() or char in _CORRELATION_EXTRA_CHARS)
        for char in candidate
    ):
        return "opaque"
    return candidate


# NOTE — `extract_menu`'s docstring is deliberately ONE plain line, unlike every
# other docstring in this module.  A route endpoint's docstring is not private:
# the LangGraph server hands it to Starlette's `SchemaGenerator`, which YAML-
# parses it and PUBLISHES it at `/openapi.json` and `/docs` to anyone who can
# reach the port.  A Google-style docstring is not valid YAML either, so it also
# produced a `yaml.scanner.ScannerError` traceback on every startup.  The prose
# that would live there:
#
#   Serves POST /internal/menu/extract.  A catch-all wrapper around
#   `_handle_extract` — no exception may reach Starlette's default 500 handler,
#   which formats the exception and would thereby render whatever a provider
#   error quoted back at us (see the module docstring, rule 3).
#
#   The correlation token is resolved here, INSIDE the try, and passed down: it
#   has to be on the catch-all's log line too, and computing it in the `except`
#   block would put a call that can raise where nothing is left to catch it.
#
#   Args:    request -- the inbound request.
#   Returns: 200 with {"items": [...], "flags": [...]}, or an
#            {"error": "<category>"} body with a 4xx/5xx status.
async def extract_menu(request: Request) -> JSONResponse:
    """Serve the isolated menu-extraction route (contract in the module docstring)."""
    correlation = "none"
    try:
        correlation = _correlation_log_token(request)
        return await _handle_extract(request, correlation)
    except Exception as exc:
        # Type name only — see the module docstring on __context__.
        _log.error(
            "menu extraction request failed unexpectedly (correlation=%s, %s)",
            correlation,
            type(exc).__name__,
        )
        return _error("internal_error", 500)


async def _handle_extract(request: Request, correlation: str) -> JSONResponse:
    """Authenticate, validate, extract, and shape the response.

    Ordered cheapest-and-most-hostile first, and the ORDER IS LOAD-BEARING: the
    auth and media-type gates both sit ahead of :func:`_read_bounded_body`, so a
    rejected caller never gets this process to pull a single byte off the
    socket.  ``test_401_never_reads_the_request_body`` pins that, and
    ``test_503_never_reads_the_request_body`` pins it for the unconfigured-route
    refusal too — that one matters more, because it is the state an unauthorized
    caller can actually reach.

    EVERY outcome carries ``correlation=`` — success, rejection and failure
    alike.  A failure line nobody can join to the .NET client's own line is a
    mystery on a background lane with no operator watching, and ``artifact=`` does
    not fill the gap: the client omits ``artifact_ref`` above 256 characters,
    which is every real inline menu, so ``artifact=none`` is the normal case.

    Args:
        request: The inbound request.
        correlation: The sanitized correlation token from
            :func:`_correlation_log_token`.  Already bounded and filtered — it is
            caller-supplied and must never reach a log line raw.

    Returns:
        The response described by :func:`extract_menu`.
    """
    # Both refusals this can produce — 401 and the 503 for an unconfigured route —
    # are raised INSIDE this helper, so the ordering property below still holds
    # for both: neither has read a byte of the body.
    rejection = _auth_rejection(request, correlation)
    if rejection is not None:
        return rejection

    if not _is_json_request(request):
        _log.warning(
            "menu extraction request rejected: non-JSON content type (correlation=%s)",
            correlation,
        )
        return _error("unsupported_media_type", 415)

    body = await _read_bounded_body(request)
    if body is None:
        _log.warning(
            "menu extraction request rejected: body over the byte budget "
            "(correlation=%s)",
            correlation,
        )
        return _error("payload_too_large", 413)

    try:
        payload = ExtractRequest.model_validate_json(body)
    except ValidationError:
        # The ValidationError message quotes the offending input; it is neither
        # logged nor returned.  "Which field" is not worth leaking a document.
        _log.warning(
            "menu extraction request rejected: body failed validation (correlation=%s)",
            correlation,
        )
        return _error("invalid_request", 422)

    artifact = _artifact_log_token(payload.artifact_ref)

    if not payload.menu_text.strip():
        _log.warning(
            "menu extraction request rejected: empty menu_text "
            "(artifact=%s, correlation=%s)",
            artifact,
            correlation,
        )
        return _error("invalid_request", 422)

    text_bytes = len(payload.menu_text.encode("utf-8"))
    if text_bytes > MAX_MENU_TEXT_BYTES:
        _log.warning(
            "menu extraction request rejected: menu_text over the cap "
            "(artifact=%s, correlation=%s)",
            artifact,
            correlation,
        )
        return _error("payload_too_large", 413)

    # Cleared per request: never inherit a value from a copied context.
    _extraction_outcome.set(None)

    agent = get_extraction_agent()
    try:
        result = await agent.extract(payload.menu_text)
    except ModelNotConfigured:
        # An operator problem, not a model problem — distinguishable only
        # because model_azure deliberately does not wrap this one.
        _log.error(
            "menu extraction unavailable: model not configured "
            "(artifact=%s, correlation=%s)",
            artifact,
            correlation,
        )
        return _error("model_not_configured", 503)
    except MenuExtractionModelError:
        _log.warning(
            "menu extraction failed (artifact=%s, correlation=%s)",
            artifact,
            correlation,
        )
        return _error("extraction_failed", 502)

    result = _with_dropped_row_flag(result, correlation)
    envelope = _sanctioned_envelope(result)

    _log.info(
        "menu extraction succeeded "
        "(artifact=%s, correlation=%s, bytes=%d, items=%d, flags=%d)",
        artifact,
        correlation,
        text_bytes,
        len(envelope["items"]),
        len(envelope["flags"]),
    )
    return JSONResponse(envelope)


def _sanctioned_envelope(result: ExtractionResult) -> dict[str, Any]:
    """Re-validate the agent's return value before it becomes the response.

    The closed response shape is THE reason this seam is a dedicated route
    rather than a graph on the Run API (D-A / §3): the wire envelope must be
    structurally incapable of carrying a privileged action.  A type annotation
    is a promise, not a check, and the agent is an injected dependency — so the
    result is round-tripped through :class:`ExtractionResult` (``extra="forbid"``)
    on the way out.  An extra key such as ``tool_calls``, however it got there,
    fails validation and becomes a ``500``; it can never ride out on a ``200``.

    Args:
        result: What the agent returned, after flag augmentation.

    Returns:
        The JSON-ready ``{"items": [...], "flags": [...]}`` mapping.

    Raises:
        pydantic.ValidationError: if the value does not validate as an
            ``ExtractionResult``.  Deliberately uncaught here — it is
            :func:`extract_menu`'s catch-all that turns it into a ``500``,
            because there is no safe way to answer ``200`` at that point.
    """
    return ExtractionResult.model_validate(result.model_dump()).model_dump(mode="json")


def _with_dropped_row_flag(
    result: ExtractionResult, correlation: str
) -> ExtractionResult:
    """Append a document-level ``rows_dropped`` flag when rows were lost.

    A new :class:`ExtractionResult` is built rather than the existing one
    mutated, so the response shape stays exactly ``items`` + ``flags`` — the
    signal rides the field that already exists for owner review instead of
    widening the contract.

    Args:
        result: What the agent returned.
        correlation: The sanitized correlation token, so the drop warning joins
            the same request's other log lines.

    Returns:
        *result* unchanged, or a copy carrying one extra flag.
    """
    outcome = _extraction_outcome.get()
    if outcome is None or outcome.dropped_rows <= 0:
        return result
    _log.warning(
        "menu extraction dropped %d of %d rows; flagging for owner review "
        "(correlation=%s)",
        outcome.dropped_rows,
        outcome.total_rows,
        correlation,
    )
    return ExtractionResult(
        items=result.items,
        flags=[*result.flags, ExtractionFlag(item_id=None, reason=ROWS_DROPPED_FLAG)],
    )


#: The app ``langgraph.json``'s ``http.app`` key mounts.  Its routes are merged
#: into the LangGraph server's own, so this must stay a single narrowly-named
#: route: anything broader could shadow a built-in (``/ok``, ``/info``, ``/runs``).
app = Starlette(routes=[Route(EXTRACT_PATH, extract_menu, methods=["POST"])])
