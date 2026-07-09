"""Spec 14 §11 / §24.4 — job envelopes must be structurally unable to carry
secrets: every hand-authored job-event schema is closed
(additionalProperties: false) and defines no credential-named property, so a
producer cannot add a token/password/api-key field without failing this test.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

import pytest


_CREDENTIAL_NAME = re.compile(
    r"(?i)(secret|password|token|api_key|apikey|credential|authorization|"
    r"private_key|card_number|pan\b|cvv)"
)

# Justified exceptions — additions here need a written reason (merge gate).
_ALLOWLISTED_PROPERTIES = frozenset(
    {
        # Tokenized Stripe payment-LINK reference (spec 09 deposit-link model):
        # a short-lived, URL-safe reference DELIVERED to the customer — its
        # documented contract is "never a PAN or CVV" and delivering it is the
        # notification's purpose. Not credential material.
        ("notification.requested.v1.json", "payment_link_token"),
    }
)


def _repo_root() -> Path:
    current = Path(__file__).resolve()
    for parent in current.parents:
        if (parent / "nx.json").exists():
            return parent
    raise AssertionError("repo root (nx.json) not found")


_JOB_EVENTS_DIR = _repo_root() / "libs" / "shared-schemas" / "schemas" / "job-events"
_SCHEMAS = sorted(_JOB_EVENTS_DIR.glob("*.json"))


def _property_names(node: object, path: str = "") -> list[tuple[str, str]]:
    """Collect every (json-path, property-name) pair declared in the schema."""
    found: list[tuple[str, str]] = []
    if isinstance(node, dict):
        for key, value in node.items():
            if key == "properties" and isinstance(value, dict):
                found.extend((f"{path}.properties", prop) for prop in value)
            found.extend(_property_names(value, f"{path}.{key}"))
    elif isinstance(node, list):
        for index, item in enumerate(node):
            found.extend(_property_names(item, f"{path}[{index}]"))
    return found


@pytest.mark.unit
def test_job_event_schemas_exist() -> None:
    assert len(_SCHEMAS) > 0, f"no job-event schemas found under {_JOB_EVENTS_DIR}"


@pytest.mark.unit
@pytest.mark.parametrize("schema_path", _SCHEMAS, ids=[s.name for s in _SCHEMAS])
def test_job_event_schema_declares_no_credential_fields(schema_path: Path) -> None:
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    offenders = [
        (where, name)
        for where, name in _property_names(schema)
        if _CREDENTIAL_NAME.search(name)
        and (schema_path.name, name) not in _ALLOWLISTED_PROPERTIES
    ]
    assert not offenders, (
        f"{schema_path.name} declares credential-shaped properties "
        f"(§24.4 — envelopes must never carry secrets): {offenders}"
    )
