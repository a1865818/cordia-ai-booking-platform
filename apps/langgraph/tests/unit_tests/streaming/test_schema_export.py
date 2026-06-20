"""Tests for exporting streaming frame contracts into shared-schemas."""

from __future__ import annotations

import importlib.util
import json
from pathlib import Path
from typing import TYPE_CHECKING

import pytest

from cordia_agent.state import StreamFrameType
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


REPO_ROOT = Path(__file__).resolve().parents[5]
EXPORTER_PATH = REPO_ROOT / "tools" / "scripts" / "export_pydantic_schemas.py"
GENERATED_TS_DIR = REPO_ROOT / "libs" / "shared-schemas" / "generated" / "typescript"

STREAMING_VARIANTS = [
    AudioStartedFrame,
    TranscriptPartialFrame,
    TranscriptFinalFrame,
    AgentTextDeltaFrame,
    AgentTextFinalFrame,
    ToolProgressFrame,
    TtsAudioDeltaFrame,
    BargeInFrame,
    TurnCancelledFrame,
]
GENERATED_TS_INTERFACE_NAMES = {
    TtsAudioDeltaFrame: "TTSAudioDeltaFrame",
}
PLACEHOLDER_NAMES = {"HelloStreamMessage", "HelloMessageCode"}

if TYPE_CHECKING:
    from types import ModuleType


def _load_exporter() -> ModuleType:
    spec = importlib.util.spec_from_file_location("export_pydantic_schemas", EXPORTER_PATH)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.mark.unit
def test_masked_dtmf_signal_is_registered_as_model_not_stream_frame() -> None:
    """Masked DTMF input stays out of the 9-variant streaming frame registry."""
    exporter = _load_exporter()

    assert MaskedDtmfSignal in exporter.MODEL_REGISTRY["models"]
    assert MaskedDtmfSignal not in exporter.MODEL_REGISTRY["streaming"]
    assert len(exporter.MODEL_REGISTRY["streaming"]) == 9


@pytest.mark.unit
def test_streaming_frames_and_enum_are_registered() -> None:
    """The exporter owns the §5.2 streaming contract roots."""
    exporter = _load_exporter()

    assert exporter.MODEL_REGISTRY["streaming"] == STREAMING_VARIANTS
    assert StreamFrameType in exporter.ENUM_REGISTRY

    registered_names = {
        item.__name__
        for item in [
            *exporter.MODEL_REGISTRY["streaming"],
            *exporter.ENUM_REGISTRY,
        ]
    }
    assert registered_names.isdisjoint(PLACEHOLDER_NAMES)


@pytest.mark.unit
def test_exporter_emits_clean_streaming_contract(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    """A clean export emits every frame wrapper and removes stale placeholders."""
    exporter = _load_exporter()
    schemas_dir = tmp_path / "schemas"
    shared_dir = schemas_dir / "shared"
    streaming_dir = schemas_dir / "streaming"
    enums_dir = schemas_dir / "enums"
    streaming_dir.mkdir(parents=True)
    enums_dir.mkdir(parents=True)
    (streaming_dir / "HelloStreamMessage.json").write_text("{}")
    (enums_dir / "HelloMessageCode.json").write_text("{}")

    langgraph_json = tmp_path / "langgraph.json"
    langgraph_json.write_text(json.dumps({"graphs": {"agent": "./cordia_agent/graph.py"}}))

    monkeypatch.setattr(exporter, "SCHEMAS_DIR", schemas_dir)
    monkeypatch.setattr(exporter, "SHARED_DIR", shared_dir)
    monkeypatch.setattr(exporter, "SHARED_DEFS_FILE", shared_dir / "PydanticSharedDefs.json")
    monkeypatch.setattr(exporter, "LANGGRAPH_JSON", langgraph_json)
    monkeypatch.setattr(exporter, "REPO_ROOT", tmp_path)

    exporter.main()
    capsys.readouterr()

    expected_wrappers = {f"{variant.__name__}.json" for variant in STREAMING_VARIANTS}
    assert {path.name for path in streaming_dir.glob("*.json")} == expected_wrappers
    assert not (streaming_dir / "HelloStreamMessage.json").exists()
    assert not (enums_dir / "HelloMessageCode.json").exists()

    stream_frame_type = json.loads((enums_dir / "StreamFrameType.json").read_text())
    assert stream_frame_type["enum"] == [member.value for member in StreamFrameType]

    shared_defs = json.loads((shared_dir / "PydanticSharedDefs.json").read_text())["$defs"]
    for variant in STREAMING_VARIANTS:
        definition = shared_defs[variant.__name__]
        assert definition["additionalProperties"] is False
        assert definition["properties"]["ts"]["type"] == "string"
        assert definition["properties"]["ts"]["format"] == "date-time"


@pytest.mark.unit
def test_generated_typescript_exports_streaming_frames_when_available() -> None:
    """After shared-schemas generation, TS/Zod expose the frame contract."""
    index_ts = GENERATED_TS_DIR / "index.ts"
    zod_ts = GENERATED_TS_DIR / "zod.ts"
    if not index_ts.exists() or not zod_ts.exists():
        pytest.skip("Run `bun nx run shared-schemas:generate-types` first.")

    index_content = index_ts.read_text()
    zod_content = zod_ts.read_text()

    for variant in STREAMING_VARIANTS:
        interface_name = GENERATED_TS_INTERFACE_NAMES.get(variant, variant.__name__)
        assert f"export interface {interface_name}" in index_content
        assert f"export const {variant.__name__}Schema = z.object" in zod_content

    assert "ts: string;" in index_content
    assert '"ts": z.string().datetime({ offset: true })' in zod_content
    assert "TranscriptFinalFrameSchema = z.object" in zod_content
    assert ".strict();" in zod_content
