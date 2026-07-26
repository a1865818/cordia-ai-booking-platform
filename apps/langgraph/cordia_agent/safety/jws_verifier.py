"""Production HS256 compact-JWS verifier for signed ``TenantContext`` tokens (spec-15 Phase 5).

Byte-for-byte interoperable with the ``apps/api`` C# signer
(``JwsTenantContextSigner``, Phase 2): identical compact-JWS wire form
``base64url(header).base64url(payload).base64url(signature)`` with
``header = {"alg":"HS256","kid":<id>,"typ":"tenantctx+jws"}`` and an HMAC-SHA256
signature over the JWS signing input (``b64url(header) + "." + b64url(payload)``).

Verification recomputes the HMAC over the **received** base64 signing input and
never re-serialises the payload, so canonical-JSON / datetime formatting
differences between the two independent signer implementations can never break
interop.

This module is a fully independent production implementation.  It must NEVER
import the test stub (``tests/_stubs/tenant_context_stub.py``).
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import os
from datetime import UTC, datetime, timedelta
from typing import TYPE_CHECKING

from cordia_agent.safety.tenant_context import (
    InvalidTenantContext,
    TenantContext,
    get_verifier,
    register_verifier,
)


if TYPE_CHECKING:
    from collections.abc import Callable


# Environment variable carrying the active verification key set, as a JSON object
# mapping ``kid -> secret`` (each secret is the raw HMAC key string; its UTF-8
# bytes are the HMAC key).  See :func:`keys_from_config` for the deployment
# contract with the apps/api signer.
SIGNING_KEYS_ENV_VAR = "CORDIA_TENANT_CONTEXT_SIGNING_KEYS"

_ALG = "HS256"
_TYP = "tenantctx+jws"
_DEFAULT_SKEW_SECONDS = 60


def _b64url_decode(segment: str) -> bytes:
    """base64url-decode *segment*, re-padding as needed (JWS convention)."""
    padding = "=" * (-len(segment) % 4)
    return base64.urlsafe_b64decode(segment + padding)


def _b64url(raw: bytes) -> str:
    """base64url-encode *raw* with padding stripped (JWS convention)."""
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()


def _default_now() -> datetime:
    """Return the current instant as a timezone-aware UTC ``datetime``."""
    return datetime.now(UTC)


class JwsTenantContextVerifier:
    """Production HS256 compact-JWS verifier implementing ``TenantContextVerifier``.

    Holds an active-key set (``kid -> secret``) so a token signed under any kid
    still present in the set verifies — supporting zero-downtime key rotation
    (publish the new kid, flip the signer, then retire the old kid).  The clock is
    injectable so tests can pin time; the default reads real UTC now.

    Never logs the secret or the token.  Every failure path raises
    :class:`InvalidTenantContext` with a generic message (no secret/claim leakage).
    """

    def __init__(
        self,
        keys: dict[str, str],
        *,
        now: Callable[[], datetime] | None = None,
        skew_seconds: int = _DEFAULT_SKEW_SECONDS,
    ) -> None:
        """Construct the verifier.

        Args:
            keys: Active verification key set mapping ``kid`` to its raw HMAC
                secret string (UTF-8 bytes are the HMAC key).  Must be non-empty.
            now: Callable returning the current timezone-aware instant; defaults
                to real UTC now.  Injectable so tests can pin the clock.
            skew_seconds: Allowed clock skew (seconds) applied to the
                ``expires_at`` / ``issued_at`` checks (default 60, matching the
                apps/api signer).

        Raises:
            ValueError: if *keys* is empty (fail closed — a verifier with no key
                could never accept any token and would mask misconfiguration).
        """
        if not keys:
            raise ValueError(
                "JwsTenantContextVerifier requires at least one signing key"
            )
        self._keys = dict(keys)
        self._now = now if now is not None else _default_now
        self._skew = timedelta(seconds=skew_seconds)

    def verify(self, signed_token: str) -> TenantContext:
        """Verify *signed_token* and return the decoded ``TenantContext``.

        Verification order (fail closed at each step):
            1. split into exactly 3 compact-JWS parts;
            2. decode the header; require ``alg == "HS256"`` and
               ``typ == "tenantctx+jws"``; read ``kid``;
            3. ``kid`` must be in the active-key set;
            4. recompute HMAC-SHA256 over the RECEIVED signing input and compare
               constant-time to the received signature (never re-serialises the
               payload);
            5. decode + JSON-parse the payload and validate it as a
               ``TenantContext`` (frozen / extra-forbid enforced by the model);
            6. reject if expired or issued in the future, allowing clock skew.

        Raises:
            InvalidTenantContext: on any failure above.  Messages are generic and
                never contain the secret, the token, or claim values.
        """
        if not signed_token:
            raise InvalidTenantContext("Malformed token: empty")

        parts = signed_token.split(".")
        if len(parts) != 3:
            raise InvalidTenantContext(
                "Malformed token: expected 3-part compact JWS "
                "(header.payload.signature)"
            )
        header_b64, payload_b64, sig_b64 = parts

        # (2) Header: decode, pin alg + typ, read kid.
        try:
            header = json.loads(_b64url_decode(header_b64))
        except Exception as exc:
            raise InvalidTenantContext("Malformed token header") from exc
        if not isinstance(header, dict):
            raise InvalidTenantContext("Malformed token header: not an object")
        if header.get("alg") != _ALG:
            # Alg is pinned — no alg-confusion / downgrade.
            raise InvalidTenantContext("Unsupported token alg")
        if header.get("typ") != _TYP:
            raise InvalidTenantContext("Unexpected token typ")

        # (3) kid must be in the active-key set (unknown / retired kid rejected).
        kid = header.get("kid")
        if not isinstance(kid, str) or kid not in self._keys:
            raise InvalidTenantContext("Unknown signing key id")
        secret = self._keys[kid]

        # (4) Recompute the HMAC over the RECEIVED signing input and compare in
        # constant time.  Never re-serialise the payload.
        signing_input = f"{header_b64}.{payload_b64}".encode()
        expected_sig_b64 = _b64url(
            hmac.new(secret.encode(), signing_input, hashlib.sha256).digest()
        )
        if not hmac.compare_digest(sig_b64, expected_sig_b64):
            raise InvalidTenantContext(
                "Token signature verification failed (forged or tampered)"
            )

        # (5) Decode + parse the payload, then validate the claim set.
        try:
            payload_bytes = _b64url_decode(payload_b64)
        except Exception as exc:
            raise InvalidTenantContext("Malformed token payload") from exc
        try:
            claims_dict = json.loads(payload_bytes)
        except json.JSONDecodeError as exc:
            raise InvalidTenantContext("Token payload is not valid JSON") from exc
        try:
            ctx = TenantContext.model_validate(claims_dict)
        except Exception as exc:
            raise InvalidTenantContext(
                "Token payload fails TenantContext validation"
            ) from exc

        # (6) Temporal validity (with skew), using the injected clock.
        now = self._now()
        if now - self._skew > ctx.expires_at:
            raise InvalidTenantContext("Token has expired")
        if ctx.issued_at - self._skew > now:
            raise InvalidTenantContext("Token issued_at is in the future")

        return ctx


def keys_from_config() -> dict[str, str]:
    """Load the active ``TenantContext`` verification key set from the environment.

    Reads the JSON object in ``CORDIA_TENANT_CONTEXT_SIGNING_KEYS`` mapping each
    ``kid`` to its raw HMAC secret string.  The verifier uses ``secret.encode()``
    (UTF-8) as the HMAC key, so the same secret **bytes** must be provisioned on
    the ``apps/api`` signer, where they are stored base64-encoded under
    ``Secrets:TenantContextSigningKeys:Keys:<kid>``.  In other words, for each
    ``kid``: ``base64decode(apps/api value) == utf8(this value)``.

    Example::

        CORDIA_TENANT_CONTEXT_SIGNING_KEYS='{"vector-kid-1":"spec15-cross-impl-vector-secret"}'

    Returns:
        A non-empty mapping of ``kid`` to secret string.

    Raises:
        RuntimeError: if the variable is missing/empty, is not a JSON object, or
            contains any non-string / empty entry.  Fails closed — never returns
            an empty set that would register a no-key verifier.
    """
    raw = os.environ.get(SIGNING_KEYS_ENV_VAR, "").strip()
    if not raw:
        raise RuntimeError(
            f"{SIGNING_KEYS_ENV_VAR} is not set; refusing to start the TenantContext "
            "verifier with no signing keys (fail closed)"
        )
    try:
        parsed = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise RuntimeError(
            f"{SIGNING_KEYS_ENV_VAR} must be a JSON object mapping kid -> secret"
        ) from exc
    if not isinstance(parsed, dict) or not parsed:
        raise RuntimeError(
            f"{SIGNING_KEYS_ENV_VAR} must be a non-empty JSON object mapping "
            "kid -> secret"
        )
    keys: dict[str, str] = {}
    for kid, secret in parsed.items():
        if not isinstance(kid, str) or not isinstance(secret, str) or not secret:
            raise RuntimeError(
                f"{SIGNING_KEYS_ENV_VAR} entries must be non-empty string "
                "kid -> string secret"
            )
        keys[kid] = secret
    return keys


def bootstrap_verifier() -> None:
    """Register the production ``JwsTenantContextVerifier`` once at graph startup.

    Mirrors the env-guarded module-load pattern of ``init_metrics``
    (:mod:`cordia_agent.graph`):

    * When ``CORDIA_TENANT_CONTEXT_SIGNING_KEYS`` is present, construct and
      register the verifier from :func:`keys_from_config` — raising on malformed
      config (fail closed: a misconfigured production process crashes at startup
      rather than silently accepting no keys).
    * When the variable is absent (local dev / tests / CI without the feature),
      return without registering, leaving ``get_verifier()`` to fail closed with
      its "not wired" error.  This keeps importing the graph side-effect-free in
      environments that have not provisioned signing keys.

    Idempotent: if a ``JwsTenantContextVerifier`` is already registered this is a
    no-op, so re-running the bootstrap (e.g. a graph module re-import) does not
    double-register.
    """
    if not os.environ.get(SIGNING_KEYS_ENV_VAR, "").strip():
        return

    try:
        existing = get_verifier()
    except RuntimeError:
        existing = None
    if isinstance(existing, JwsTenantContextVerifier):
        return

    register_verifier(JwsTenantContextVerifier(keys_from_config()))
