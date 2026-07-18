"""Unit tests for cordia_agent.observability.redaction.

Spec refs: §25.3 (default-deny sensitive classes), Inv-12 (no raw card data),
§8 (fail-open).
"""

from __future__ import annotations

import logging

import pytest

from cordia_agent.observability.redaction import (
    RedactionLogFilter,
    install_redaction_filter,
    redact,
)
from cordia_agent.streaming.logging_redaction import REDACTION_TOKEN


# ---------------------------------------------------------------------------
# redact() — mapping with sensitive keys
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_sensitive_keys_are_redacted() -> None:
    payload = {
        "pan": "4111111111111111",
        "cvv": "123",
        "secret": "hunter2",
        "transcript": "caller said: I want to book a table",
        "customer_name": "Ada",
    }
    result = redact(payload)

    assert result["pan"] == REDACTION_TOKEN
    assert result["cvv"] == REDACTION_TOKEN
    assert result["secret"] == REDACTION_TOKEN
    assert result["transcript"] == REDACTION_TOKEN
    # Non-sensitive field must survive unchanged.
    assert result["customer_name"] == "Ada"


@pytest.mark.unit
def test_sensitive_values_replaced_not_present() -> None:
    payload = {
        "pan": "4111111111111111",
        "cvv": "123",
        "secret": "hunter2",
        "transcript": "caller said...",
    }
    result = redact(payload)
    # No original sensitive value should survive in the result.
    for v in result.values():
        assert v != "4111111111111111"
        assert v != "123"
        assert v != "hunter2"
        assert v != "caller said..."


@pytest.mark.unit
def test_redaction_token_present_for_sensitive_keys() -> None:
    payload = {
        "password": "p@ssw0rd",
        "api_key": "sk-12345",
        "authorization": "Bearer token",
    }
    result = redact(payload)
    for v in result.values():
        assert v == REDACTION_TOKEN


@pytest.mark.unit
def test_non_sensitive_fields_survive() -> None:
    payload = {
        "customer_name": "Ada",
        "party_size": 4,
        "date": "2026-07-01",
        "note": "window seat please",
    }
    result = redact(payload)
    assert result == payload


# ---------------------------------------------------------------------------
# PAN-pattern masking in non-sensitive fields
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_pan_string_in_non_sensitive_field_is_masked() -> None:
    """A bare PAN in a non-denylisted field must be masked."""
    payload = {"user_input": "4111 1111 1111 1111"}
    result = redact(payload)
    assert result["user_input"] == REDACTION_TOKEN


@pytest.mark.unit
def test_pan_without_spaces_in_non_sensitive_field_is_masked() -> None:
    payload = {"note": "my card is 5500000000000004"}
    result = redact(payload)
    assert result["note"] == REDACTION_TOKEN


@pytest.mark.unit
def test_non_pan_digit_string_survives() -> None:
    """Short digit strings (phone, code) must NOT be masked."""
    payload = {"phone": "0412345678", "code": "1234"}
    result = redact(payload)
    # 10-digit phone: not a PAN (< 13 digits for a PAN run)
    assert result["phone"] == "0412345678"


# ---------------------------------------------------------------------------
# Nested structures
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_nested_dict_redaction() -> None:
    payload = {
        "outer": {
            "pan": "4111111111111111",
            "safe": "value",
        }
    }
    result = redact(payload)
    assert result["outer"]["pan"] == REDACTION_TOKEN
    assert result["outer"]["safe"] == "value"


@pytest.mark.unit
def test_list_of_dicts_redaction() -> None:
    payload = [
        {"pan": "4111111111111111", "name": "Ada"},
        {"secret": "abc", "city": "Sydney"},
    ]
    result = redact(payload)
    assert result[0]["pan"] == REDACTION_TOKEN
    assert result[0]["name"] == "Ada"
    assert result[1]["secret"] == REDACTION_TOKEN
    assert result[1]["city"] == "Sydney"


# ---------------------------------------------------------------------------
# Case-insensitive key matching
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_sensitive_key_case_insensitive() -> None:
    payload = {"PAN": "4111111111111111", "API_KEY": "secret-key", "Safe": "ok"}
    result = redact(payload)
    assert result["PAN"] == REDACTION_TOKEN
    assert result["API_KEY"] == REDACTION_TOKEN
    assert result["Safe"] == "ok"


# ---------------------------------------------------------------------------
# Fail-open: redact never raises
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_redact_does_not_raise_on_weird_input() -> None:
    # redact should be robust to unusual input types.
    assert redact(None) is None
    assert redact(42) == 42
    assert redact("hello") == "hello"
    assert redact([]) == []


# ---------------------------------------------------------------------------
# DTMF / existing streaming_logging_redaction behaviour still passes
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_dtmf_digit_fields_still_redacted_via_observability_redact() -> None:
    """Ensure the expanded redact() still handles DTMF fields correctly."""
    payload = {"digits": "1234", "safe": "value"}
    result = redact(payload)
    # digits is in the expanded denylist (inherits SENSITIVE_DIGIT_FIELD_NAMES).
    assert result["digits"] == REDACTION_TOKEN
    assert result["safe"] == "value"


# ---------------------------------------------------------------------------
# RedactionLogFilter
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_redaction_log_filter_scrubs_msg_string() -> None:
    handler = logging.StreamHandler()
    filt = RedactionLogFilter()
    handler.addFilter(filt)

    record = logging.LogRecord(
        name="test",
        level=logging.INFO,
        pathname="",
        lineno=0,
        msg="user pan is 4111111111111111",
        args=(),
        exc_info=None,
    )
    filt.filter(record)
    # The PAN in msg should be masked.
    assert "4111111111111111" not in str(record.msg)


@pytest.mark.unit
def test_redaction_log_filter_scrubs_dict_msg() -> None:
    filt = RedactionLogFilter()
    record = logging.LogRecord(
        name="test",
        level=logging.INFO,
        pathname="",
        lineno=0,
        msg={"secret": "hunter2", "safe": "ok"},
        args=(),
        exc_info=None,
    )
    filt.filter(record)
    assert isinstance(record.msg, dict)
    assert record.msg["secret"] == REDACTION_TOKEN
    assert record.msg["safe"] == "ok"


@pytest.mark.unit
def test_redaction_log_filter_always_returns_true() -> None:
    """Filter must never suppress a record."""
    filt = RedactionLogFilter()
    record = logging.LogRecord(
        name="test",
        level=logging.WARNING,
        pathname="",
        lineno=0,
        msg="innocuous message",
        args=(),
        exc_info=None,
    )
    assert filt.filter(record) is True


@pytest.mark.unit
def test_install_redaction_filter_is_idempotent() -> None:
    """install_redaction_filter must not add duplicate filters."""
    logger = logging.getLogger("test.redaction.idempotent")
    logger.filters.clear()
    install_redaction_filter(logger)
    install_redaction_filter(logger)
    redaction_filters = [f for f in logger.filters if isinstance(f, RedactionLogFilter)]
    assert len(redaction_filters) == 1


@pytest.mark.unit
def test_install_redaction_filter_attaches_to_given_logger() -> None:
    logger = logging.getLogger("test.redaction.given")
    logger.filters.clear()
    install_redaction_filter(logger)
    assert any(isinstance(f, RedactionLogFilter) for f in logger.filters)
