"""Schema round-trip drift guard (§27.1, ticket 03-G).

Goal: fail if any owned contract model or enum is missing from the Pydantic→JSON Schema
export, catching silent contract drift before it reaches shared-schemas consumers.

Design (IMPORTANT — do NOT change):
- Does NOT spawn bun/nx inside pytest (flaky, needs bun in PATH). Instead:
  1. Loads ``tools/scripts/export_pydantic_schemas.py`` in-process via importlib,
     which defines MODEL_REGISTRY and ENUM_REGISTRY without running main().
  2. Asserts the owned contracts are present in the registries (source-of-truth guard).
  3. Asserts the generated output files exist on disk (output drift guard).

CI/Verification flow (documented here, not executed here):
  The verification step runs ``bun nx run langgraph:generate-schemas`` BEFORE this
  test so the generated files in libs/shared-schemas/schemas/ are current. If they
  are stale or missing, the output drift assertions below will fail the build.

Invariants asserted:
- ToolResultEnvelope, PolicyValidationResult, OperationStatusView ∈ MODEL_REGISTRY["models"]
- AvailabilityMode + all per-tool status enums + VerificationAction ∈ ENUM_REGISTRY
- libs/shared-schemas/schemas/shared/PydanticSharedDefs.json contains "ToolResultEnvelope"
- schemas/models/PolicyValidationResult.json + OperationStatusView.json exist
- schemas/enums/AvailabilityMode.json exists
"""

from __future__ import annotations

import importlib.util
import sys
from pathlib import Path


# ---------------------------------------------------------------------------
# Load the exporter module in-process (no subprocess / no bun/nx)
# ---------------------------------------------------------------------------

# Compute repo root relative to this test file:
# tests/unit_tests/tools/test_schema_roundtrip_guard.py
# → tests/unit_tests/tools/ → tests/unit_tests/ → tests/ → apps/langgraph/ → apps/ → repo_root
_THIS_FILE = Path(__file__).resolve()
_REPO_ROOT = _THIS_FILE.parents[5]  # …/Cordia

_EXPORTER_PATH = _REPO_ROOT / "tools" / "scripts" / "export_pydantic_schemas.py"
_SCHEMAS_DIR = _REPO_ROOT / "libs" / "shared-schemas" / "schemas"


def _load_exporter():
    """Load export_pydantic_schemas.py in-process without running main().

    Adds the apps/langgraph directory to sys.path so cordia_agent imports resolve.
    Returns the loaded module object with MODEL_REGISTRY and ENUM_REGISTRY attributes.
    """
    langgraph_root = _REPO_ROOT / "apps" / "langgraph"
    langgraph_str = str(langgraph_root)
    if langgraph_str not in sys.path:
        sys.path.insert(0, langgraph_str)

    spec = importlib.util.spec_from_file_location(
        "export_pydantic_schemas", _EXPORTER_PATH
    )
    assert spec is not None and spec.loader is not None, (
        f"Could not create module spec for {_EXPORTER_PATH}. "
        "Ensure tools/scripts/export_pydantic_schemas.py exists."
    )
    module = importlib.util.module_from_spec(spec)
    # Execute the module (defines registries + imports; does NOT call main())
    spec.loader.exec_module(module)  # type: ignore[union-attr]
    return module


# ---------------------------------------------------------------------------
# Required contracts — fail clearly if any are missing or renamed
# ---------------------------------------------------------------------------

# Models that must be present in MODEL_REGISTRY["models"]
REQUIRED_MODELS = frozenset(
    {
        "ToolResultEnvelope",
        "PolicyValidationResult",
        "OperationStatusView",
    }
)

# Enums that must be present in ENUM_REGISTRY
REQUIRED_ENUMS = frozenset(
    {
        "AvailabilityMode",
        # Per-tool status enums
        "GetTenantContextStatus",
        "FindCustomerContextStatus",
        "CatalogItemStatus",
        "ValidateBookingDraftStatus",
        "CheckCandidateAvailabilityStatus",
        "RequestVerificationStatus",
        "CheckVerificationStatus",
        "SubmitConfirmedIntentStatus",
        "RequestHumanHandoffStatus",
        "FindManageableBookingsStatus",
        "GetBookingDetailsStatus",
        "ValidateBookingChangeStatus",
        "SubmitBookingModificationStatus",
        "SubmitBookingCancellationStatus",
        # Cross-service enums
        "VerificationAction",
    }
)

# Generated output files that must exist on disk after generate-schemas runs
REQUIRED_OUTPUT_FILES = [
    _SCHEMAS_DIR / "shared" / "PydanticSharedDefs.json",
    _SCHEMAS_DIR / "models" / "PolicyValidationResult.json",
    _SCHEMAS_DIR / "models" / "OperationStatusView.json",
    _SCHEMAS_DIR / "enums" / "AvailabilityMode.json",
]

# Content guard: PydanticSharedDefs.json must mention ToolResultEnvelope
_SHARED_DEFS_CONTENT_GUARD = "ToolResultEnvelope"


# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------


import pytest  # noqa: E402  (must follow module-level setup above)


@pytest.mark.unit
class TestSchemaRegistryGuard:
    """Source-of-truth drift guard: owned contracts must be in the exporter registries."""

    def test_exporter_module_loads_without_error(self) -> None:
        """The exporter module must import cleanly without running main()."""
        module = _load_exporter()
        assert hasattr(module, "MODEL_REGISTRY"), "MODEL_REGISTRY must be defined"
        assert hasattr(module, "ENUM_REGISTRY"), "ENUM_REGISTRY must be defined"

    def test_required_models_in_model_registry(self) -> None:
        """ToolResultEnvelope, PolicyValidationResult, OperationStatusView in MODEL_REGISTRY['models']."""
        module = _load_exporter()
        registry: dict = module.MODEL_REGISTRY

        assert "models" in registry, "MODEL_REGISTRY must have a 'models' key"
        registered_model_titles = {cls.__name__ for cls in registry["models"]}

        missing = REQUIRED_MODELS - registered_model_titles
        assert not missing, (
            f"The following owned contract models are MISSING from MODEL_REGISTRY['models']: "
            f"{sorted(missing)}. "
            "This means a contract has been removed or renamed — update the exporter."
        )

    def test_required_enums_in_enum_registry(self) -> None:
        """AvailabilityMode, per-tool status enums, VerificationAction in ENUM_REGISTRY."""
        module = _load_exporter()
        registry: list = module.ENUM_REGISTRY

        registered_enum_names = {cls.__name__ for cls in registry}

        missing = REQUIRED_ENUMS - registered_enum_names
        assert not missing, (
            f"The following owned contract enums are MISSING from ENUM_REGISTRY: "
            f"{sorted(missing)}. "
            "This means an enum has been removed or renamed — update the exporter."
        )

    def test_all_required_models_across_all_registry_categories(self) -> None:
        """Ensure each required model appears in at least one MODEL_REGISTRY category."""
        module = _load_exporter()
        registry: dict = module.MODEL_REGISTRY

        all_registered = {
            cls.__name__ for models in registry.values() for cls in models
        }

        missing = REQUIRED_MODELS - all_registered
        assert not missing, (
            f"These owned models are absent from ALL MODEL_REGISTRY categories: "
            f"{sorted(missing)}"
        )


@pytest.mark.unit
class TestSchemaOutputFilesGuard:
    """Output drift guard: generated schema files must exist on disk.

    IMPORTANT: these tests assume ``bun nx run langgraph:generate-schemas`` has been
    run before executing the test suite. In CI and the verification sequence, the
    generate-schemas step is always run first (see verification instructions in the
    ticket). If these fail locally, regenerate first:
        bun nx run langgraph:generate-schemas
    """

    def test_shared_defs_file_exists(self) -> None:
        """PydanticSharedDefs.json must exist in shared/."""
        path = _SCHEMAS_DIR / "shared" / "PydanticSharedDefs.json"
        assert path.exists(), (
            f"Missing generated file: {path}. "
            "Run 'bun nx run langgraph:generate-schemas' to regenerate."
        )

    def test_shared_defs_contains_tool_result_envelope(self) -> None:
        """PydanticSharedDefs.json must reference ToolResultEnvelope (content guard)."""
        path = _SCHEMAS_DIR / "shared" / "PydanticSharedDefs.json"
        if not path.exists():
            pytest.skip(f"Skipping content check — file missing: {path}")

        content = path.read_text()
        assert _SHARED_DEFS_CONTENT_GUARD in content, (
            f"PydanticSharedDefs.json does not contain '{_SHARED_DEFS_CONTENT_GUARD}'. "
            "ToolResultEnvelope may have been removed or renamed in the exporter."
        )

    def test_policy_validation_result_schema_exists(self) -> None:
        """schemas/models/PolicyValidationResult.json must exist."""
        path = _SCHEMAS_DIR / "models" / "PolicyValidationResult.json"
        assert path.exists(), (
            f"Missing generated file: {path}. "
            "Run 'bun nx run langgraph:generate-schemas' to regenerate."
        )

    def test_operation_status_view_schema_exists(self) -> None:
        """schemas/models/OperationStatusView.json must exist."""
        path = _SCHEMAS_DIR / "models" / "OperationStatusView.json"
        assert path.exists(), (
            f"Missing generated file: {path}. "
            "Run 'bun nx run langgraph:generate-schemas' to regenerate."
        )

    def test_availability_mode_enum_schema_exists(self) -> None:
        """schemas/enums/AvailabilityMode.json must exist."""
        path = _SCHEMAS_DIR / "enums" / "AvailabilityMode.json"
        assert path.exists(), (
            f"Missing generated file: {path}. "
            "Run 'bun nx run langgraph:generate-schemas' to regenerate."
        )

    def test_all_required_output_files_exist(self) -> None:
        """Aggregate check: all required generated files exist."""
        missing_files = [str(p) for p in REQUIRED_OUTPUT_FILES if not p.exists()]
        assert not missing_files, (
            "The following generated schema files are missing:\n"
            + "\n".join(f"  {f}" for f in missing_files)
            + "\nRun 'bun nx run langgraph:generate-schemas' to regenerate."
        )
