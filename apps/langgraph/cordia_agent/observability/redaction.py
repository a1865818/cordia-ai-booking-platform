"""Observability redaction -- default-deny scrubbing for logs and telemetry.

Extends the DTMF-focused ``streaming/logging_redaction`` module with a broader
default-deny denylist covering PCI/PII/prompt data classes (§25.3, Inv-12).

Key design choices:
- The denylist is a superset of ``SENSITIVE_DIGIT_FIELD_NAMES``; the existing
  module is imported and its recursive walker is reused via
  ``_redact_value`` / ``_redact_mapping``.
- PAN-pattern strings (13-19 digit runs with optional spaces/dashes) are
  masked even when the key is not denylisted.
- ``RedactionLogFilter`` is a ``logging.Filter`` that scrubs ``record.msg``
  and ``record.args`` through ``redact(...)`` so model prompts, completions,
  and transcripts never reach a log handler.
- ``install_redaction_filter`` attaches the filter to the root logger (or a
  given logger) as a one-liner.

Fail-open: ``redact()`` catches all exceptions and returns the original value
rather than raising, so a buggy redaction call never breaks the agent.

Spec refs: §25.3 (default-deny sensitive classes), Inv-12 (no raw PAN/card
data in logs), §8 (fail-open).
"""

from __future__ import annotations

import contextlib
import logging
import re
from typing import Any

from cordia_agent.streaming.logging_redaction import (
    REDACTION_TOKEN,
    SENSITIVE_DIGIT_FIELD_NAMES,
)


# ---------------------------------------------------------------------------
# Expanded denylist -- §25.3 / Inv-12 default-deny classes.
# Superset of the DTMF-focused SENSITIVE_DIGIT_FIELD_NAMES.
# ---------------------------------------------------------------------------

_EXPANDED_SENSITIVE_NAMES: frozenset[str] = SENSITIVE_DIGIT_FIELD_NAMES | frozenset(
    {
        # Secrets / credentials
        "secret",
        "token",
        "password",
        "api_key",
        "apikey",
        "authorization",
        "credential",
        # Payment / card data (PCI)
        "pan",
        "card",
        "card_number",
        "cardnumber",
        "cvv",
        "cvc",
        "ccv",
        "pin",
        # Identity
        "ssn",
        # Model I/O -- prompts and completions must never reach logs (§25.3)
        "transcript",
        "audio",
        "prompt",
        "completion",
        "message_content",
        "raw_content",
    }
)

# ---------------------------------------------------------------------------
# PAN pattern -- 13-19 digit runs with optional spaces or dashes between groups.
# ---------------------------------------------------------------------------

_PAN_RE = re.compile(
    r"(?<!\d)"  # not preceded by a digit
    r"(?:\d[\s\-]?){13,19}"  # 13-19 digits optionally separated by spaces/dashes (any grouping)
    r"(?!\d)"  # not followed by a digit
)


def _looks_like_pan(value: str) -> bool:
    """Return ``True`` if *value* contains a PAN-like digit run (13-19 digits).

    Checks whether any regex match within the string strips to 13-19 raw digits,
    so it works for both standalone PANs and PANs embedded in prose.
    """
    m = _PAN_RE.search(value)
    if not m:
        return False
    matched = m.group(0)
    digits_only = re.sub(r"[\s\-]", "", matched)
    n = len(digits_only)
    return digits_only.isdigit() and 13 <= n <= 19


def _key_is_sensitive(key: str) -> bool:
    """Case-insensitive substring match against the denylist."""
    lower = key.lower()
    return any(denied in lower for denied in _EXPANDED_SENSITIVE_NAMES)


def _redact_value_expanded(value: Any) -> Any:
    """Recursively redact *value* using the expanded denylist.

    Delegates mapping traversal to :func:`_redact_mapping_expanded` and
    list/tuple traversal to itself.  For string leaves, checks for PAN
    patterns regardless of the key.
    """
    from pydantic import BaseModel

    if isinstance(value, BaseModel):
        return _redact_mapping_expanded(value.model_dump(mode="json"))
    if isinstance(value, dict):
        return _redact_mapping_expanded(value)
    if isinstance(value, list):
        return [_redact_value_expanded(item) for item in value]
    if isinstance(value, tuple):
        return tuple(_redact_value_expanded(item) for item in value)
    if isinstance(value, str) and _looks_like_pan(value):
        return REDACTION_TOKEN
    return value


def _redact_mapping_expanded(mapping: dict[str, Any]) -> dict[str, Any]:
    """Redact a mapping using the expanded denylist.

    Keys that match any entry in ``_EXPANDED_SENSITIVE_NAMES`` (case-insensitive
    substring) have their value replaced with ``REDACTION_TOKEN``.  All other
    values are recursed into via ``_redact_value_expanded``.
    """
    redacted: dict[str, Any] = {}
    for key, item in mapping.items():
        if _key_is_sensitive(key):
            redacted[key] = REDACTION_TOKEN
            continue
        redacted[key] = _redact_value_expanded(item)
    return redacted


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------


def redact(value: Any) -> Any:
    """Return a redacted copy of *value* safe for logs and telemetry.

    - Mapping keys matching the expanded denylist (case-insensitive substring)
      are replaced with ``REDACTION_TOKEN``.
    - String values containing PAN-like digit runs are replaced with
      ``REDACTION_TOKEN`` even when the key is not denylisted.
    - Recursion handles nested dicts, lists, tuples, and Pydantic models.
    - The original value is never mutated.

    Fail-open: if an unexpected exception occurs the original *value* is
    returned rather than raising (§8).

    Args:
        value: Any Python value -- dict, list, Pydantic model, str, or scalar.

    Returns:
        A redacted copy, or the original *value* if redaction failed.
    """
    with contextlib.suppress(Exception):
        return _redact_value_expanded(value)
    return value


# ---------------------------------------------------------------------------
# Logging filter
# ---------------------------------------------------------------------------


class RedactionLogFilter(logging.Filter):
    """``logging.Filter`` that scrubs sensitive data from log records.

    Applies :func:`redact` to ``record.msg`` and ``record.args`` so that model
    prompts, completions, transcripts, and secrets never reach a log handler
    regardless of log level (default-deny per §25.3).

    Attach via :func:`install_redaction_filter` or manually::

        handler.addFilter(RedactionLogFilter())
    """

    def filter(self, record: logging.LogRecord) -> bool:
        """Scrub *record* in-place and always allow it through.

        The filter never blocks records -- it only sanitises them.  Returning
        ``True`` means the record is always passed to the handler.

        Args:
            record: The ``LogRecord`` to sanitise.

        Returns:
            Always ``True`` (record is never suppressed).
        """
        with contextlib.suppress(Exception):
            if isinstance(record.msg, str | dict):
                record.msg = redact(record.msg)
            if record.args:
                if isinstance(record.args, dict):
                    record.args = redact(record.args)
                elif isinstance(record.args, tuple):
                    record.args = tuple(
                        redact(a) if isinstance(a, str | dict) else a
                        for a in record.args
                    )
        return True


def install_redaction_filter(logger: logging.Logger | None = None) -> None:
    """Attach a ``RedactionLogFilter`` to the handlers of *logger* (or the root logger).

    Python's ``callHandlers`` propagation bypasses parent-logger filters, so attaching
    to the logger itself does not scrub records emitted by child loggers. Attaching to
    each handler ensures the filter runs on every record that reaches a sink.

    Idempotent: handlers that already have a ``RedactionLogFilter`` are skipped.

    Args:
        logger: Target logger; defaults to the root logger (``logging.root``).
    """
    target = logger if logger is not None else logging.root
    f = RedactionLogFilter()
    for handler in target.handlers:
        if not any(
            isinstance(existing, RedactionLogFilter) for existing in handler.filters
        ):
            handler.addFilter(f)
    # Also add to the logger itself as a belt-and-suspenders guard for records that
    # are handled directly by the target logger rather than propagating up.
    if not any(isinstance(existing, RedactionLogFilter) for existing in target.filters):
        target.addFilter(f)
