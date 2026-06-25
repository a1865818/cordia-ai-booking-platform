"""Revision manager package (spec 02 §8.7 / Inv-10)."""

from cordia_agent.revisions.manager import (
    build_revision,
    canonical_payload_hash,
    is_material_change,
    supersede_revision,
)


__all__ = [
    "build_revision",
    "canonical_payload_hash",
    "is_material_change",
    "supersede_revision",
]
