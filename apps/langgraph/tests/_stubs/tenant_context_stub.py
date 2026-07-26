"""FROZEN SPEC-15 CONTRACT FAKE — do not import from production.

This module is a test-only stub for the TenantContext verifier port.  It signs
and verifies **compact HS256 JWS** tokens over a canonical JSON serialisation of
the claim set — the exact wire form the production signer (``apps/api`` Phase 2)
and production verifier (``apps/langgraph`` Phase 5) must byte-match.

Token form: ``base64url(header).base64url(payload).base64url(signature)`` where
``header = {"alg":"HS256","kid":<kid>,"typ":"tenantctx+jws"}``, ``payload`` is the
canonical JSON of the claim set, and the signature is
``HMAC_SHA256(secret, base64url(header) + "." + base64url(payload))`` — the
standard JWS signing input (the two base64url segments joined by a literal ``.``).

DO NOT import this module from production code.  It must only be referenced from
test fixtures (register_verifier / clear_verifier calls).

When spec 15 lands, the production verifier mirrors this contract exactly (same
canonical JSON, same signing input); this stub stays as the interop reference.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
from typing import Any

from cordia_agent.safety.tenant_context import InvalidTenantContext, TenantContext


# Default HMAC secret / kid for the stub — tests may override via kwargs.
_DEFAULT_SECRET = "stub-secret-do-not-use-in-production"
_DEFAULT_KID = "stub-kid"
_TYP = "tenantctx+jws"


def _canonical_json(obj: dict[str, Any]) -> bytes:
    """Produce a deterministic, compact JSON encoding of *obj*.

    ``ensure_ascii=True`` is part of the frozen contract: the C# signer (Phase 2)
    byte-matches this exact serialisation.  Do NOT relax it.
    """
    return json.dumps(
        obj, sort_keys=True, separators=(",", ":"), ensure_ascii=True
    ).encode()


def _b64url(raw: bytes) -> str:
    """base64url-encode *raw* with padding stripped (JWS convention)."""
    return base64.urlsafe_b64encode(raw).rstrip(b"=").decode()


def _b64url_decode(segment: str) -> bytes:
    """base64url-decode *segment*, re-padding as needed."""
    padding = "=" * (-len(segment) % 4)
    return base64.urlsafe_b64decode(segment + padding)


def sign(
    claims: dict[str, Any] | TenantContext,
    *,
    secret: str = _DEFAULT_SECRET,
    kid: str = _DEFAULT_KID,
) -> str:
    """Sign *claims* as a compact HS256 JWS and return the 3-part token.

    Token format: ``<b64url(header)>.<b64url(payload)>.<b64url(signature)>``
    with ``header = {"alg":"HS256","kid":<kid>,"typ":"tenantctx+jws"}`` and the
    signature computed as HMAC-SHA256 over the JWS signing input
    (``b64url(header) + "." + b64url(payload)``).

    Args:
        claims: A dict of TenantContext fields, or a ``TenantContext`` instance
            (serialised via ``model_dump(mode="json")``).
        secret: HMAC secret; defaults to the stub default.
        kid: Key id placed in the JWS header; defaults to the stub default.

    Returns:
        A signed token string suitable for passing as ``signed_tenant_context``.
    """
    if isinstance(claims, TenantContext):
        claims_dict: dict[str, Any] = claims.model_dump(mode="json")
    else:
        claims_dict = dict(claims)

    header = {"alg": "HS256", "kid": kid, "typ": _TYP}
    header_b64 = _b64url(_canonical_json(header))
    payload_b64 = _b64url(_canonical_json(claims_dict))

    signing_input = f"{header_b64}.{payload_b64}".encode()
    sig_bytes = hmac.new(secret.encode(), signing_input, hashlib.sha256).digest()
    sig_b64 = _b64url(sig_bytes)

    return f"{header_b64}.{payload_b64}.{sig_b64}"


class StubTenantContextVerifier:
    """FROZEN SPEC-15 CONTRACT FAKE — test-only HMAC-SHA256 JWS verifier.

    Implements the ``TenantContextVerifier`` port over compact HS256 JWS.
    Header-aware: constructed with a single default secret plus an optional
    ``{kid: secret}`` map, so a future real verifier can mirror multi-key
    rotation.  Register this in test fixtures; clear in teardown.
    """

    def __init__(
        self,
        secret: str = _DEFAULT_SECRET,
        *,
        keys: dict[str, str] | None = None,
    ) -> None:
        self._default_secret = secret
        self._keys = dict(keys) if keys else {}

    def _secret_for(self, kid: str | None) -> str:
        """Return the secret for *kid* from the key map, else the default."""
        if kid is not None and kid in self._keys:
            return self._keys[kid]
        return self._default_secret

    def verify(self, signed_token: str) -> TenantContext:
        """Verify *signed_token* and return the decoded ``TenantContext``.

        Parses the 3-part JWS, checks the header ``typ``/``kid``, recomputes the
        HMAC over the JWS signing input, constant-time compares, then validates
        the payload as a ``TenantContext``.

        Raises:
            InvalidTenantContext: if the token is malformed, the header ``typ`` is
                wrong, the signature does not match, or the payload cannot be
                parsed as a ``TenantContext``.
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

        try:
            header = json.loads(_b64url_decode(header_b64))
        except Exception as exc:
            raise InvalidTenantContext(f"Malformed token header: {exc}") from exc
        if not isinstance(header, dict):
            raise InvalidTenantContext("Malformed token header: not an object")
        if header.get("typ") != _TYP:
            raise InvalidTenantContext(f"Unexpected token typ: {header.get('typ')!r}")

        secret = self._secret_for(header.get("kid"))

        # Recompute the signature over the RECEIVED signing input and compare
        # in constant time.
        signing_input = f"{header_b64}.{payload_b64}".encode()
        expected_sig_b64 = _b64url(
            hmac.new(secret.encode(), signing_input, hashlib.sha256).digest()
        )
        if not hmac.compare_digest(sig_b64, expected_sig_b64):
            raise InvalidTenantContext(
                "Token signature verification failed (forged or tampered)"
            )

        try:
            payload_bytes = _b64url_decode(payload_b64)
        except Exception as exc:
            raise InvalidTenantContext(f"Malformed token payload: {exc}") from exc

        try:
            claims_dict: dict[str, Any] = json.loads(payload_bytes)
        except json.JSONDecodeError as exc:
            raise InvalidTenantContext(
                f"Token payload is not valid JSON: {exc}"
            ) from exc

        try:
            return TenantContext.model_validate(claims_dict)
        except Exception as exc:
            raise InvalidTenantContext(
                f"Token payload fails TenantContext validation: {exc}"
            ) from exc
