"""Tenant-scoped session checkpointer for AgentState persistence (§8.9).

Establishes the persistence boundary for downstream nodes (CaptureFields,
AwaitConfirmation): checkpoint after every material slot update and BEFORE
first submit.

This module holds **no** booking side effects — no HTTP, no tool imports,
no submission client.  It is the injection seam (§8.9): production backs the
store with a LangGraph checkpointer over a tenant-scoped session store; tests
inject a plain ``dict``.

Invariants enforced here:
  Inv-2  — ``requires_pre_submit_checkpoint`` / ``is_pre_submit_checkpoint_durable``
           form the contract surface that 02-J will consume.  This module does NOT
           implement submit.
  Inv-13 — tenant-scoped keys guarantee that two tenants sharing a session_id
           cannot collide (``tenant_scoped_key``).
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from cordia_agent.state import AgentState


if TYPE_CHECKING:
    from collections.abc import MutableMapping


class SessionCheckpointer:
    """Persist and reload ``AgentState`` under a tenant-scoped session key.

    The ``store`` parameter is the injection seam (§8.9): production backs this
    with a LangGraph checkpointer over the tenant-scoped session store; tests
    pass a plain ``dict``.  This class holds **no** booking side effects — no
    HTTP, no booking-tool imports, no submission client.

    Invariants:
      Inv-13 — ``tenant_scoped_key`` prevents cross-tenant collision even when
               ``session_id`` values are reused across tenants.
      Inv-2  — ``requires_pre_submit_checkpoint`` / ``is_pre_submit_checkpoint_durable``
               form the pre-submit guard surface (02-J); submit is NOT here.
    """

    def __init__(self, store: MutableMapping[str, str] | None = None) -> None:
        """Initialise with an optional backing store.

        Args:
            store: A ``MutableMapping[str, str]`` used as the persistence
                backend.  Defaults to an empty ``dict`` (suitable for tests and
                single-process use).  Production should inject a durable,
                tenant-scoped store (§8.9).
        """
        self._store: MutableMapping[str, str] = store if store is not None else {}

    @staticmethod
    def tenant_scoped_key(tenant_id: str, session_id: str) -> str:
        """Return a key that is unique per (tenant, session) pair.

        Using ``"{tenant_id}::{session_id}"`` ensures two tenants with the
        same ``session_id`` never collide (Inv-13).

        Args:
            tenant_id: The tenant identifier.
            session_id: The dialog session identifier.

        Returns:
            A composite string key of the form ``"<tenant_id>::<session_id>"``.
        """
        return f"{tenant_id}::{session_id}"

    def save(self, state: AgentState) -> str:
        """Persist ``state`` under its tenant-scoped key and return that key.

        Raises ``ValueError`` if either ``state.tenant_id`` or
        ``state.session_id`` is absent/empty — an unscoped state cannot be
        checkpointed (Inv-13).

        Args:
            state: The ``AgentState`` to persist.

        Returns:
            The tenant-scoped key under which the state was stored.

        Raises:
            ValueError: If ``tenant_id`` or ``session_id`` is missing.
        """
        if not state.tenant_id:
            raise ValueError(
                "Cannot checkpoint an AgentState with no tenant_id (Inv-13)."
            )
        if not state.session_id:
            raise ValueError(
                "Cannot checkpoint an AgentState with no session_id (Inv-13)."
            )

        key = self.tenant_scoped_key(state.tenant_id, state.session_id)
        self._store[key] = state.model_dump_json()
        return key

    def load(self, tenant_id: str, session_id: str) -> AgentState | None:
        """Reload the ``AgentState`` for *(tenant_id, session_id)*.

        Args:
            tenant_id: The tenant identifier.
            session_id: The dialog session identifier.

        Returns:
            The deserialized ``AgentState``, or ``None`` if no record exists.
        """
        key = self.tenant_scoped_key(tenant_id, session_id)
        raw = self._store.get(key)
        if raw is None:
            return None
        return AgentState.model_validate_json(raw)

    def checkpoint_material_update(self, state: AgentState) -> str:
        """Checkpoint after a material slot update; call from CaptureFields/AwaitConfirmation.

        Idempotent: writing the same state twice yields identical stored bytes
        and a single record — no duplication, no corruption.  Delegates to
        :meth:`save`.

        Args:
            state: The ``AgentState`` after a material field update.

        Returns:
            The tenant-scoped key used.
        """
        return self.save(state)

    def is_pre_submit_checkpoint_durable(self, state: AgentState) -> bool:
        """Return ``True`` iff the stored record durably captures *this* op+idem pair.

        Durable proof (Inv-2) requires ALL of:
        1. A record exists for (tenant_id, session_id).
        2. The stored record's ``operation_id`` matches ``state.operation_id``
           (and is non-``None``).
        3. The stored record's ``idempotency_key`` matches ``state.idempotency_key``
           (and is non-``None``).

        Args:
            state: The current ``AgentState``.

        Returns:
            ``True`` if the pre-submit checkpoint is durable; ``False`` otherwise.
        """
        if not state.tenant_id or not state.session_id:
            return False
        if state.operation_id is None or state.idempotency_key is None:
            return False

        stored = self.load(state.tenant_id, state.session_id)
        if stored is None:
            return False

        return (
            stored.operation_id == state.operation_id
            and stored.idempotency_key == state.idempotency_key
        )

    def requires_pre_submit_checkpoint(self, state: AgentState) -> bool:
        """Return ``True`` iff a pre-submit checkpoint is still required (Inv-2 surface).

        This is the guard 02-J consumes: ``True`` means "DO NOT submit yet —
        the op_id+idem_key pair has not been durably persisted."  This method
        is the contract surface only; it does **not** implement submit.

        Equivalent to ``not self.is_pre_submit_checkpoint_durable(state)``.

        Args:
            state: The current ``AgentState``.

        Returns:
            ``True`` if a checkpoint is still required before submitting.
        """
        return not self.is_pre_submit_checkpoint_durable(state)
