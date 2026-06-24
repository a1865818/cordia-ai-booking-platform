"""Unit tests for SessionCheckpointer (§8.9, Inv-2, Inv-13).

Covers:
  - round-trip persistence (byte-stable)
  - tenant isolation (Inv-13): same session_id, different tenant_id → distinct records
  - material-update idempotency: double-write produces one record, identical bytes
  - pre-submit guard (Inv-2 surface): requires_pre_submit_checkpoint /
    is_pre_submit_checkpoint_durable semantics
  - no side effects: checkpoint module imports no HTTP/tool dependencies
  - ValueError on save with missing tenant_id or session_id
"""

from __future__ import annotations

import inspect

import pytest

from cordia_agent.checkpoint import SessionCheckpointer
from cordia_agent.state import (
    AgentState,
    AvailabilityMode,
    BookingDraft,
    BookingRequest,
    Channel,
    OperationType,
    SourceInfo,
)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _make_draft(
    tenant_id: str = "tenant_a", session_id: str = "session_1"
) -> BookingDraft:
    """Build a minimal valid BookingDraft."""
    return BookingDraft(
        tenant_id=tenant_id,
        session_id=session_id,
        schema_version="1.0",
        tenant_policy_version="v2",
        request=BookingRequest(operation=OperationType.create),
        source=SourceInfo(channel=Channel.voice, language="en-AU"),
    )


def _make_state(
    tenant_id: str = "tenant_a",
    session_id: str = "session_1",
    operation_id: str | None = "op_001",
    idempotency_key: str | None = "idem_abc",
    last_confirmed_revision: int | None = 3,
    availability_mode: AvailabilityMode | None = AvailabilityMode.HELD,
) -> AgentState:
    """Build a populated AgentState suitable for checkpointing."""
    return AgentState(
        tenant_id=tenant_id,
        session_id=session_id,
        draft=_make_draft(tenant_id=tenant_id, session_id=session_id),
        operation=OperationType.create,
        operation_id=operation_id,
        idempotency_key=idempotency_key,
        last_confirmed_revision=last_confirmed_revision,
        availability_mode=availability_mode,
    )


# ---------------------------------------------------------------------------
# Round-trip
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_round_trip_is_byte_stable() -> None:
    """save then load returns the same state with identical JSON bytes."""
    cp = SessionCheckpointer()
    original = _make_state()

    cp.save(original)
    loaded = cp.load("tenant_a", "session_1")

    assert loaded is not None
    assert loaded == original
    assert loaded.model_dump_json() == original.model_dump_json()


# ---------------------------------------------------------------------------
# Tenant isolation (Inv-13)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_tenant_isolation_same_session_different_tenants() -> None:
    """Two states with the same session_id but different tenant_ids do not collide (Inv-13)."""
    cp = SessionCheckpointer()
    session_id = "shared_session"

    state_a = _make_state(tenant_id="tenant_a", session_id=session_id)
    state_b = _make_state(
        tenant_id="tenant_b", session_id=session_id, operation_id="op_999"
    )

    cp.save(state_a)
    cp.save(state_b)

    loaded_a = cp.load("tenant_a", session_id)
    loaded_b = cp.load("tenant_b", session_id)

    assert loaded_a is not None
    assert loaded_b is not None
    assert loaded_a.tenant_id == "tenant_a"
    assert loaded_b.tenant_id == "tenant_b"
    assert loaded_a.operation_id == "op_001"
    assert loaded_b.operation_id == "op_999"
    assert loaded_a != loaded_b


@pytest.mark.unit
def test_tenant_scoped_key_differs_across_tenants() -> None:
    """tenant_scoped_key must differ for different tenants even with the same session_id."""
    key_a = SessionCheckpointer.tenant_scoped_key("tenant_a", "session_x")
    key_b = SessionCheckpointer.tenant_scoped_key("tenant_b", "session_x")
    assert key_a != key_b


# ---------------------------------------------------------------------------
# Material-update idempotency
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_checkpoint_material_update_is_idempotent() -> None:
    """Double write with the same state yields one record, identical bytes, no duplication."""
    store: dict[str, str] = {}
    cp = SessionCheckpointer(store=store)
    state = _make_state()

    key1 = cp.checkpoint_material_update(state)
    bytes_after_first = store[key1]

    key2 = cp.checkpoint_material_update(state)
    bytes_after_second = store[key2]

    assert key1 == key2
    assert len(store) == 1
    assert bytes_after_first == bytes_after_second


# ---------------------------------------------------------------------------
# Pre-submit guard (Inv-2 surface)
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_requires_pre_submit_before_any_save() -> None:
    """With op_id+idem_key set but before any save, requires_pre_submit_checkpoint is True."""
    cp = SessionCheckpointer()
    state = _make_state()

    assert cp.requires_pre_submit_checkpoint(state) is True
    assert cp.is_pre_submit_checkpoint_durable(state) is False


@pytest.mark.unit
def test_durable_after_checkpoint_material_update() -> None:
    """After checkpoint_material_update, is_pre_submit_checkpoint_durable is True."""
    cp = SessionCheckpointer()
    state = _make_state()

    cp.checkpoint_material_update(state)

    assert cp.is_pre_submit_checkpoint_durable(state) is True
    assert cp.requires_pre_submit_checkpoint(state) is False


@pytest.mark.unit
def test_stale_stored_op_id_still_requires_checkpoint() -> None:
    """A stored record with a DIFFERENT op_id (stale) means requires_pre_submit is True."""
    cp = SessionCheckpointer()

    stale_state = _make_state(operation_id="op_OLD")
    cp.checkpoint_material_update(stale_state)

    # New state has a different operation_id — stored record is stale.
    new_state = _make_state(operation_id="op_NEW")

    assert cp.requires_pre_submit_checkpoint(new_state) is True
    assert cp.is_pre_submit_checkpoint_durable(new_state) is False


@pytest.mark.unit
def test_requires_pre_submit_when_op_id_is_none() -> None:
    """State without operation_id is always considered to require a checkpoint."""
    cp = SessionCheckpointer()
    state = _make_state(operation_id=None, idempotency_key=None)

    assert cp.requires_pre_submit_checkpoint(state) is True


# ---------------------------------------------------------------------------
# No side effects
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_no_http_or_tool_imports_in_checkpoint_module() -> None:
    """The checkpoint module must not import any HTTP or tool dependencies.

    Asserts that the source text contains none of the forbidden symbols AND
    that no such module leaked into the module namespace.
    """
    import cordia_agent.checkpoint as cp_module

    src = inspect.getsource(cp_module)

    forbidden_in_source = (
        "httpx",
        "requests",
        "aiohttp",
        "cordia_agent.tools",
        "submit_confirmed_intent",
    )
    for banned in forbidden_in_source:
        assert banned not in src, (
            f"Forbidden dependency '{banned}' found in checkpoint module source."
        )

    # Also assert none of these leaked into the module namespace.
    module_names = dir(cp_module)
    for banned in ("httpx", "requests", "aiohttp"):
        assert banned not in module_names, (
            f"Forbidden module '{banned}' found in checkpoint module namespace."
        )


# ---------------------------------------------------------------------------
# ValueError on missing scope fields
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_save_raises_on_missing_tenant_id() -> None:
    """save raises ValueError if tenant_id is absent (Inv-13)."""
    cp = SessionCheckpointer()
    state = AgentState(session_id="session_1")

    with pytest.raises(ValueError, match="tenant_id"):
        cp.save(state)


@pytest.mark.unit
def test_save_raises_on_empty_tenant_id() -> None:
    """save raises ValueError if tenant_id is an empty string (Inv-13)."""
    cp = SessionCheckpointer()
    state = AgentState(tenant_id="", session_id="session_1")

    with pytest.raises(ValueError, match="tenant_id"):
        cp.save(state)


@pytest.mark.unit
def test_save_raises_on_missing_session_id() -> None:
    """save raises ValueError if session_id is absent (Inv-13)."""
    cp = SessionCheckpointer()
    state = AgentState(tenant_id="tenant_a")

    with pytest.raises(ValueError, match="session_id"):
        cp.save(state)


@pytest.mark.unit
def test_save_raises_on_empty_session_id() -> None:
    """save raises ValueError if session_id is an empty string (Inv-13)."""
    cp = SessionCheckpointer()
    state = AgentState(tenant_id="tenant_a", session_id="")

    with pytest.raises(ValueError, match="session_id"):
        cp.save(state)


# ---------------------------------------------------------------------------
# load returns None for absent key
# ---------------------------------------------------------------------------


@pytest.mark.unit
def test_load_returns_none_when_no_record() -> None:
    """load returns None if no checkpoint exists for the given (tenant_id, session_id)."""
    cp = SessionCheckpointer()
    result = cp.load("tenant_x", "session_nonexistent")
    assert result is None
