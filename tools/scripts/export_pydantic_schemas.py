#!/usr/bin/env python
"""Export Pydantic models from cordia_agent to JSON Schema files.

Pipeline: Pydantic (source of truth) -> JSON Schema -> C# + TypeScript/Zod.

Strategy (mirrors the shared-$defs pattern from the reference monorepo):
  * Every model's nested definitions are merged into a single
    ``schemas/shared/PydanticSharedDefs.json`` so cross-references resolve
    through one canonical file.
  * Each registered model is emitted as a lightweight wrapper file
    ``{ "title": <name>, "$ref": "../shared/PydanticSharedDefs.json#/$defs/<name>" }``
    in its category directory.
  * Enums are emitted as standalone JSON Schema files in ``schemas/enums/``.

This exporter OWNS the seven subdirs listed in ``OWNED_SUBDIRS`` and cleans them
on each run. ``schemas/job-events/`` (hand-authored, API-originated) is never
touched.

Run via:  BUILDING=true uv run python ../../tools/scripts/export_pydantic_schemas.py
"""

from __future__ import annotations

import json
from enum import Enum
from pathlib import Path
from typing import Any

from cordia_agent.configurations import HelloConfiguration
from cordia_agent.state import (
    BookingDraft,
    BookingRequest,
    CanonicalBookingDraft,
    CaptureMethod,
    Channel,
    FieldValidation,
    GreetingStatus,
    HelloInputState,
    HelloOutputState,
    OperationType,
    StreamFrameType,
    TriState,
)
from cordia_agent.streaming.dtmf import MaskedDtmfSignal
from cordia_agent.streaming_models import (
    AgentTextDeltaFrame,
    AgentTextFinalFrame,
    AudioStartedFrame,
    BargeInFrame,
    ToolProgressFrame,
    TranscriptFinalFrame,
    TranscriptPartialFrame,
    TtsAudioDeltaFrame,
    TurnCancelledFrame,
)
from cordia_agent.tools.contracts import ToolResultEnvelope
from cordia_agent.streaming.reconnect import NormalizedOperationStatus, ReconnectOutcome
from cordia_agent.tools.status_codes import (
    AvailabilityMode,
    CatalogItemStatus,
    CheckCandidateAvailabilityStatus,
    CheckVerificationStatus,
    FindCustomerContextStatus,
    FindManageableBookingsStatus,
    GetBookingDetailsStatus,
    GetTenantContextStatus,
    RequestHumanHandoffStatus,
    RequestVerificationStatus,
    SubmitBookingCancellationStatus,
    SubmitBookingModificationStatus,
    SubmitConfirmedIntentStatus,
    ValidateBookingChangeStatus,
    ValidateBookingDraftStatus,
)


REPO_ROOT = Path(__file__).resolve().parents[2]
SCHEMAS_DIR = REPO_ROOT / "libs" / "shared-schemas" / "schemas"
SHARED_DIR = SCHEMAS_DIR / "shared"
SHARED_DEFS_FILE = SHARED_DIR / "PydanticSharedDefs.json"
LANGGRAPH_JSON = REPO_ROOT / "apps" / "langgraph" / "langgraph.json"

# Category directory -> list of Pydantic BaseModel subclasses to export as wrappers.
MODEL_REGISTRY: dict[str, list[type]] = {
    "graph-inputs": [HelloInputState],
    "graph-outputs": [HelloOutputState],
    "configurations": [HelloConfiguration],
    "streaming": [
        AudioStartedFrame,
        TranscriptPartialFrame,
        TranscriptFinalFrame,
        AgentTextDeltaFrame,
        AgentTextFinalFrame,
        ToolProgressFrame,
        TtsAudioDeltaFrame,
        BargeInFrame,
        TurnCancelledFrame,
    ],
    "models": [
        BookingDraft,
        BookingRequest,
        CanonicalBookingDraft,
        MaskedDtmfSignal,
        ToolResultEnvelope,
    ],
}

# Standalone string enums emitted into schemas/enums/.
ENUM_REGISTRY: list[type[Enum]] = [
    GreetingStatus,
    StreamFrameType,
    TriState,
    OperationType,
    CaptureMethod,
    FieldValidation,
    Channel,
    AvailabilityMode,
    GetTenantContextStatus,
    FindCustomerContextStatus,
    CatalogItemStatus,
    ValidateBookingDraftStatus,
    CheckCandidateAvailabilityStatus,
    RequestVerificationStatus,
    CheckVerificationStatus,
    SubmitConfirmedIntentStatus,
    RequestHumanHandoffStatus,
    FindManageableBookingsStatus,
    GetBookingDetailsStatus,
    ValidateBookingChangeStatus,
    SubmitBookingModificationStatus,
    SubmitBookingCancellationStatus,
    NormalizedOperationStatus,
    ReconnectOutcome,
]

# Subdirs this exporter owns (cleaned each run). job-events/ is excluded.
OWNED_SUBDIRS = [
    "enums",
    "models",
    "graph-inputs",
    "graph-outputs",
    "configurations",
    "streaming",
    "shared",
]


def clean_owned_dirs() -> None:
    """Remove and recreate the exporter-owned subdirectories."""
    for sub in OWNED_SUBDIRS:
        d = SCHEMAS_DIR / sub
        if d.exists():
            for f in d.glob("*.json"):
                f.unlink()
        d.mkdir(parents=True, exist_ok=True)


def enum_schema(enum_cls: type[Enum]) -> dict[str, Any]:
    """Build a standalone JSON Schema for a string enum."""
    return {
        "$schema": "http://json-schema.org/draft-07/schema#",
        "title": enum_cls.__name__,
        "description": (enum_cls.__doc__ or "").strip(),
        "type": "string",
        "enum": [member.value for member in enum_cls],
    }


def collect_shared_defs() -> dict[str, Any]:
    """Build the unified $defs map from all registered models.

    For each model, ``model_json_schema()`` yields the model's own schema plus a
    ``$defs`` block of nested types. We merge every nested def and add the
    model's own schema (keyed by its title) into one shared map.
    """
    shared_defs: dict[str, Any] = {}

    for models in MODEL_REGISTRY.values():
        for model in models:
            schema = model.model_json_schema(ref_template="#/$defs/{model}")
            for name, definition in schema.pop("$defs", {}).items():
                shared_defs.setdefault(name, definition)
            title = schema.get("title", model.__name__)
            shared_defs.setdefault(title, schema)

    # Ensure every standalone enum is also present in the shared defs so that
    # internal "#/$defs/<Enum>" references inside model defs resolve.
    for enum_cls in ENUM_REGISTRY:
        shared_defs.setdefault(enum_cls.__name__, enum_schema(enum_cls))

    return dict(sorted(shared_defs.items()))


def write_shared_defs(shared_defs: dict[str, Any]) -> None:
    """Write the unified PydanticSharedDefs.json file."""
    SHARED_DIR.mkdir(parents=True, exist_ok=True)
    document = {
        "$schema": "http://json-schema.org/draft-07/schema#",
        "title": "Pydantic Shared Definitions",
        "$defs": shared_defs,
    }
    SHARED_DEFS_FILE.write_text(json.dumps(document, indent=2) + "\n")


def write_wrappers() -> list[str]:
    """Write one wrapper file per registered model. Returns the titles written."""
    written: list[str] = []
    for category, models in MODEL_REGISTRY.items():
        out_dir = SCHEMAS_DIR / category
        out_dir.mkdir(parents=True, exist_ok=True)
        for model in models:
            title = model.model_json_schema().get("title", model.__name__)
            wrapper = {
                "title": title,
                "$ref": f"../shared/PydanticSharedDefs.json#/$defs/{title}",
            }
            (out_dir / f"{title}.json").write_text(json.dumps(wrapper, indent=2) + "\n")
            written.append(title)
    return written


def write_enums() -> list[str]:
    """Write standalone enum schema files. Returns the enum names written."""
    out_dir = SCHEMAS_DIR / "enums"
    out_dir.mkdir(parents=True, exist_ok=True)
    written: list[str] = []
    for enum_cls in ENUM_REGISTRY:
        (out_dir / f"{enum_cls.__name__}.json").write_text(
            json.dumps(enum_schema(enum_cls), indent=2) + "\n"
        )
        written.append(enum_cls.__name__)
    return written


def validate_no_duplicates(names: list[str]) -> None:
    """Fail if any exported title appears more than once."""
    seen: set[str] = set()
    dupes: set[str] = set()
    for name in names:
        if name in seen:
            dupes.add(name)
        seen.add(name)
    if dupes:
        raise SystemExit(f"Duplicate exported schema names: {sorted(dupes)}")


def validate_graph_coverage() -> None:
    """Sanity check that langgraph.json registers at least one graph."""
    if not LANGGRAPH_JSON.exists():
        return
    config = json.loads(LANGGRAPH_JSON.read_text())
    graphs = config.get("graphs", {})
    if not graphs:
        raise SystemExit("langgraph.json declares no graphs.")


def main() -> None:
    print("Exporting Pydantic schemas from cordia_agent...")
    clean_owned_dirs()

    shared_defs = collect_shared_defs()
    write_shared_defs(shared_defs)
    print(f"  shared defs: {len(shared_defs)} definition(s)")

    enum_names = write_enums()
    print(f"  enums: {', '.join(enum_names)}")

    wrapper_titles = write_wrappers()
    print(f"  models: {', '.join(wrapper_titles)}")

    validate_no_duplicates(enum_names + wrapper_titles)
    validate_graph_coverage()

    print(f"Done. Schemas written to {SCHEMAS_DIR.relative_to(REPO_ROOT)}")


if __name__ == "__main__":
    main()
