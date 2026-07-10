"""Unit tests for the spec-14 pre-emit redaction filter (Inv-12, §24.2)."""

from __future__ import annotations

import pytest

from cordia_agent.contracts.security import RedactionPolicy
from cordia_agent.security.redaction import redact_payload, redact_text


MASK = "[REDACTED]"

# Luhn-valid test PANs (standard test numbers, never real cards).
VISA_PAN = "4111111111111111"
VISA_PAN_SPACED = "4111 1111 1111 1111"
VISA_PAN_DASHED = "4111-1111-1111-1111"
AMEX_PAN = "378282246310005"  # 15 digits

# 16 digits that fail a WHOLE-run Luhn check but embed a Luhn-valid 13-19
# window (1111111111112) — masked fail-closed (Inv-12), see _contains_pan.
NON_LUHN_16 = "4111111111111112"

# 16 digits with NO Luhn-valid 13-19 window anywhere — the precision anchor
# that proves the filter is not simply "mask every 16-digit run".
CLEAN_16 = "5260181590830166"


@pytest.mark.unit
def test_pan_masked_plain() -> None:
    assert redact_text(f"my card is {VISA_PAN} thanks") == f"my card is {MASK} thanks"


@pytest.mark.unit
def test_pan_masked_with_spaces_and_dashes() -> None:
    assert MASK in redact_text(f"card {VISA_PAN_SPACED} ok")
    assert MASK in redact_text(f"card {VISA_PAN_DASHED} ok")
    assert "4111" not in redact_text(f"card {VISA_PAN_SPACED} ok")


@pytest.mark.unit
def test_pan_masked_with_doubled_separators() -> None:
    """A PAN spoken/typed with a doubled space or dash still forms one candidate
    run and must not escape masking (Inv-12)."""
    assert "4111" not in redact_text("card 4111  1111  1111  1111 ok")
    assert MASK in redact_text("card 4111  1111  1111  1111 ok")
    assert "4111" not in redact_text("card 4111--1111--1111--1111 ok")
    assert MASK in redact_text("card 4111--1111--1111--1111 ok")


@pytest.mark.unit
def test_amex_15_digit_pan_masked() -> None:
    assert redact_text(f"amex {AMEX_PAN}") == f"amex {MASK}"


@pytest.mark.unit
def test_digit_run_embedding_luhn_window_is_masked() -> None:
    """A 13+ digit run embedding a Luhn-valid window masks fail-closed (Inv-12),
    even when the whole run fails Luhn — a card padded with adjacent digits
    must not escape."""
    assert redact_text(f"ref {NON_LUHN_16}") == f"ref {MASK}"
    # A 17-digit run = 16-digit PAN + one trailing pad digit (the M1 case).
    assert redact_text(f"my card is {VISA_PAN}2 please") == f"my card is {MASK} please"


@pytest.mark.unit
def test_digit_run_without_luhn_window_survives() -> None:
    """Precision anchor: a 16-digit run with NO embedded Luhn window is left
    intact — the filter is fail-closed, not indiscriminate."""
    assert redact_text(f"order {CLEAN_16} shipped") == f"order {CLEAN_16} shipped"


@pytest.mark.unit
def test_short_numbers_not_masked() -> None:
    """Party sizes, times, phone-ish shorter runs are untouched."""
    text = "table for 4 at 7:30, phone 0412345678"
    assert redact_text(text) == text


@pytest.mark.unit
def test_cvv_in_context_masked() -> None:
    assert redact_text("the CVV is 123") == f"the CVV is {MASK}"
    assert redact_text("security code: 4321") == f"security code: {MASK}"
    # A bare 3-digit number without CVV context is untouched.
    assert redact_text("party of 123 people") == "party of 123 people"


@pytest.mark.unit
def test_cvv_compact_form_masked() -> None:
    """'cvv123' with no separator must not escape masking (Inv-12)."""
    assert redact_text("cvv123") == f"cvv{MASK}"
    assert redact_text("CVC456 ok") == f"CVC{MASK} ok"


@pytest.mark.unit
def test_security_code_hyphenated_context_masked() -> None:
    """'security-code' (hyphenated) is a CVV context word too — the docstring
    lists 'security-code' as scrubbed, so it must not pass through unmasked."""
    assert redact_text("security-code: 123") == f"security-code: {MASK}"
    assert redact_text("security-code456") == f"security-code{MASK}"
    # The original spaced form still works.
    assert redact_text("security code: 4321") == f"security code: {MASK}"


@pytest.mark.unit
def test_adjacent_independent_numbers_are_not_fused_into_a_pan() -> None:
    """Two phone numbers one space apart (or a spaced date-time) must not be
    masked as one fused 20-digit 'PAN' — groups longer than 6 digits signal
    independent numbers, evaluated per-group."""
    text = "call 0412345678 0298765432 anytime"
    assert redact_text(text) == text
    stamp = "2026-07-09 18 55 30"
    assert redact_text(stamp) == stamp
    # A solid Luhn-valid PAN inside such a context is still masked.
    mixed = f"ref 0412345678 {VISA_PAN} end"
    out = redact_text(mixed)
    assert VISA_PAN not in out
    assert "0412345678" in out


@pytest.mark.unit
def test_very_long_digit_runs_mask_unconditionally() -> None:
    """>50-digit runs mask without window-scanning (fail-closed + DoS cap)."""
    blob = "9" * 60
    assert redact_text(f"blob {blob} end") == f"blob {MASK} end"


@pytest.mark.unit
def test_pan_embedded_in_longer_digit_run_is_masked() -> None:
    """A PAN inside a longer digit run must not fail open (Inv-12, fail-closed).

    Every run of >= 13 digits is window-scanned and masked wholesale when any
    13-19 digit window is Luhn-valid — so a card padded with adjacent digits
    (here 4 pad digits) cannot escape.
    """
    embedded = f"99{VISA_PAN}42"  # 20-digit run embedding a Luhn-valid PAN
    assert VISA_PAN not in redact_text(f"blob {embedded} end")
    assert MASK in redact_text(f"blob {embedded} end")


@pytest.mark.unit
def test_ssn_masked() -> None:
    assert redact_text("SSN 123-45-6789 on file") == f"SSN {MASK} on file"


@pytest.mark.unit
def test_tfn_masked() -> None:
    """AU TFN formatted 3-3-3 with separators is masked as a government id."""
    assert redact_text("my TFN is 123 456 782") == f"my TFN is {MASK}"


@pytest.mark.unit
def test_government_id_masking_can_be_disabled_but_pan_cannot() -> None:
    policy = RedactionPolicy(mask_government_id=False)
    assert redact_text("SSN 123-45-6789", policy) == "SSN 123-45-6789"
    # PAN masking stays on even if a (mis)configured policy tries to disable it.
    off = RedactionPolicy(mask_pan=False, mask_cvv=False)
    assert redact_text(f"card {VISA_PAN}", off) == f"card {MASK}"
    assert redact_text("CVV 123", off) == f"CVV {MASK}"


@pytest.mark.unit
def test_custom_mask_token() -> None:
    policy = RedactionPolicy(mask_token="***")
    assert redact_text(f"card {VISA_PAN}", policy) == "card ***"


@pytest.mark.unit
def test_redact_payload_configured_fields_and_nested_text() -> None:
    policy = RedactionPolicy(configured_sensitive_fields=["member_number"])
    payload = {
        "member_number": "M-99881",
        "notes": [f"customer read card {VISA_PAN_SPACED}"],
        "party_size": 4,
        "nested": {"member_number": "M-2", "ok": "fine"},
    }
    out = redact_payload(payload, policy)
    assert out["member_number"] == MASK
    assert out["nested"]["member_number"] == MASK
    assert out["party_size"] == 4
    assert out["nested"]["ok"] == "fine"
    assert "4111" not in out["notes"][0]
    # Input is not mutated.
    assert payload["member_number"] == "M-99881"


@pytest.mark.unit
def test_redact_payload_handles_scalars_and_none() -> None:
    assert redact_payload(None) is None
    assert redact_payload(7) == 7
    assert redact_payload(f"pan {VISA_PAN}") == f"pan {MASK}"


@pytest.mark.unit
def test_redact_payload_masks_numeric_pan_scalar_for_csharp_parity() -> None:
    """An UNQUOTED numeric PAN/SSN must not slip through just because it was a
    JSON number rather than a string (parity with C# SanitizeJson, Inv-12). The
    number is replaced with the mask STRING (a number cannot hold the token);
    benign numbers and booleans pass through untouched."""
    # A bare numeric PAN scalar is masked (type changes number -> mask string).
    assert redact_payload(int(VISA_PAN)) == MASK
    # A float PAN is masked to the FULL token too — no scientific-notation escape
    # (str(float) would give "4.11e+15") and no partial "[REDACTED].0" leaking the
    # fractional part beside the token.
    assert redact_payload(float(VISA_PAN)) == MASK
    # Inside a payload, a numeric PAN under an ordinary key is masked...
    out = redact_payload({"card": int(VISA_PAN), "party_size": 4, "vip": True})
    assert out["card"] == MASK
    # ...while a benign small number and a bool are byte-identical (bool is an
    # int subclass — it must NOT be coerced through the numeric path).
    assert out["party_size"] == 4
    assert out["vip"] is True

    # A numeric value under a control-plane identifier key round-trips (parity
    # with the C# JsonValue exemption) — a system-minted numeric id/seq/ts must
    # never be masked, even if it coincidentally embeds a Luhn-valid window.
    exempt = redact_payload({"seq": int(VISA_PAN), "ts": 1719878400000})
    assert exempt["seq"] == int(VISA_PAN)
    assert exempt["ts"] == 1719878400000


@pytest.mark.unit
def test_control_plane_keys_are_exempt_but_free_text_is_not() -> None:
    """*_id/*_key/*_hash values are system-minted and must round-trip even
    when they contain a chance Luhn-valid digit run; free-text siblings are
    still scrubbed."""
    payload = {
        "idempotency_key": "idem_ca7a996c93714390930497c7e0772c63",
        "payload_hash": "9" * 20,
        "notes": f"card {VISA_PAN}",
    }
    out = redact_payload(payload)
    assert out["idempotency_key"] == payload["idempotency_key"]
    assert out["payload_hash"] == payload["payload_hash"]
    assert VISA_PAN not in out["notes"]


@pytest.mark.unit
def test_active_policy_registry_drives_default_redaction() -> None:
    """The tenant policy installed via set_redaction_policy applies wherever
    no explicit policy argument is passed (spec 14 §4.1 loading seam)."""
    from cordia_agent.security.policy import (
        reset_redaction_policy,
        set_redaction_policy,
    )

    try:
        set_redaction_policy(
            RedactionPolicy(configured_sensitive_fields=["member_number"])
        )
        out = redact_payload({"member_number": "M-1", "other": "ok"})
        assert out["member_number"] == MASK
        assert out["other"] == "ok"
    finally:
        reset_redaction_policy()
    # After reset the baseline (empty tenant list) applies again.
    assert redact_payload({"member_number": "M-1"})["member_number"] == "M-1"
