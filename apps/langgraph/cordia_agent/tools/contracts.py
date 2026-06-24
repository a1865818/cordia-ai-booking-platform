"""Tool-contract envelope and pure-read payload models for the agent<->platform boundary.

Spec refs: 03 §4.2 (envelope), §4.3 (pure-read Out schemas), §6.1 (PolicyValidationResult).

These Pydantic models are the source of truth for the shared types exported to
C# and TypeScript/Zod via ``tools/scripts/export_pydantic_schemas.py``.
"""

from __future__ import annotations

from typing import Literal

from pydantic import AwareDatetime, BaseModel, Field

from cordia_agent.contracts.operation_status import NormalizedOperationStatus
from cordia_agent.tools.status_codes import (
    AvailabilityMode,
    ValidateBookingDraftStatus,
    VerificationAction,
)


SCHEMA_VERSION = "1.0.0"


class ToolResultEnvelope(BaseModel):
    """Single closed wrapper returned by every tool (spec 03 §4.2)."""

    model_config = {"extra": "forbid"}

    ok: bool = Field(description="Machine-readable success/failure.")
    status: str = Field(description="Per-tool status code; never raw error text.")
    customer_hint: str | None = Field(
        default=None,
        description="Safe customer-facing string; never raw provider text.",
    )
    recoverable_fields: list[str] = Field(
        default_factory=list, description="Fields the agent may re-prompt to correct."
    )
    suggested_action: str | None = Field(
        default=None,
        description="Machine hint for next move, e.g. request_alternative_time.",
    )
    data: dict | None = Field(
        default=None, description="Tool-specific typed payload (per-tool Out schemas)."
    )
    correlation_id: str = Field(
        description="Propagated from the typed tool client; consumed by observability."
    )
    schema_version: str = Field(
        default=SCHEMA_VERSION, description="Cross-language contract version."
    )


# ---------------------------------------------------------------------------
# Pure-read foreground tool output models (spec 03 §4.3)
# These ride inside ToolResultEnvelope.data.
# ---------------------------------------------------------------------------


class TenantContextView(BaseModel):
    """Output payload for get_tenant_context (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    tenant_id: str = Field(description="Stable tenant identifier.")
    policy_version: str = Field(description="Active tenant policy version.")
    timezone: str = Field(
        description="IANA timezone for the tenant, e.g. 'Australia/Sydney'."
    )
    supported_operations: list[str] = Field(
        description="Operation types supported by this tenant, e.g. ['create', 'cancel']."
    )
    capabilities: list[str] = Field(
        description="Feature-flag capability names active for this tenant."
    )
    currency: str = Field(description="ISO 4217 currency code, e.g. 'AUD'.")


class CustomerContextView(BaseModel):
    """Output payload for find_customer_context (spec 03 §4.3).

    Named CustomerContextView (not CustomerContext) to avoid clashing with
    cordia_agent.state.CustomerContext, which has a different shape.
    Minimized: no sensitive history is included here (§9.2).
    """

    model_config = {"extra": "forbid"}

    customer_id: str | None = Field(
        default=None, description="Stable customer ID where known."
    )
    display_name: str | None = Field(
        default=None, description="Customer display name if available."
    )
    usual_party_size: int | None = Field(
        default=None, description="Customer's typical party size preference."
    )
    communication_preference: str | None = Field(
        default=None, description="Preferred communication channel, e.g. 'sms'."
    )
    has_tokenized_payment: bool = Field(
        default=False, description="Whether the customer has a stored payment token."
    )


class CatalogItem(BaseModel):
    """A single catalog item returned by search_vendor_catalog / get_catalog_item (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    item_id: str = Field(description="Stable catalog item identifier.")
    name: str = Field(description="Human-readable item name.")
    description: str | None = Field(
        default=None, description="Optional item description."
    )
    price_minor: int = Field(
        description="Price in the currency's minor unit (e.g. cents for AUD)."
    )
    currency: str = Field(description="ISO 4217 currency code for price_minor.")
    catalog_version: str = Field(description="Catalog version this item was read from.")


class CatalogSearchResult(BaseModel):
    """Output payload for search_vendor_catalog (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    items: list[CatalogItem] = Field(
        default_factory=list, description="Matching catalog items; empty if none found."
    )


class PolicyValidationResult(BaseModel):
    """Output payload for validate_booking_draft (spec 03 §6.1).

    All six fields must round-trip from the spec §6.1 JSON fixture exactly.
    """

    model_config = {"extra": "forbid"}

    valid: bool = Field(description="True iff the draft passes all policy checks.")
    code: ValidateBookingDraftStatus = Field(
        description="Structured policy/validation outcome code."
    )
    customer_message: str = Field(
        description="Safe customer-facing explanation of the validation outcome."
    )
    recoverable_fields: list[str] = Field(
        default_factory=list,
        description="Draft fields the agent may re-prompt to correct.",
    )
    suggested_action: str | None = Field(
        default=None,
        description="Machine hint for the agent's next move, e.g. request_alternative_time.",
    )
    policy_version: str = Field(
        description="Tenant policy version the validation was run against."
    )


class VerificationGrantView(BaseModel):
    """Output payload for check_verification (spec 03 §4.3)."""

    model_config = {"extra": "forbid"}

    grant_id: str | None = Field(
        default=None, description="Opaque grant identifier; None if not yet issued."
    )
    tenant_id: str = Field(description="Tenant scope for this grant.")
    customer_id: str | None = Field(
        default=None, description="Customer the grant belongs to, if identified."
    )
    target_booking_id: str | None = Field(
        default=None, description="Booking this grant targets, if operation-scoped."
    )
    allowed_action: VerificationAction = Field(
        description="The single action this grant permits."
    )
    expires_at: AwareDatetime = Field(
        description="Grant expiry time (offset-aware ISO-8601)."
    )
    scope: Literal["session", "operation"] = Field(
        description="'session' for broad session scope; 'operation' for a single operation."
    )


class OperationStatusView(BaseModel):
    """Output payload for get_operation_status (spec 03 §4.3 / spec 06 D2).

    ``status`` is typed as NormalizedOperationStatus (9-value public projection,
    Inv-16). Internal substates (e.g. 'validating', 'held', 'awaiting_payment')
    are never surfaced here; Pydantic will reject them at validation time.
    """

    model_config = {"extra": "forbid"}

    operation_id: str = Field(description="Stable operation identifier.")
    status: NormalizedOperationStatus = Field(
        description=(
            "Normalized 9-value operation status (Inv-16). "
            "Internal substates are NEVER valid here."
        )
    )
    booking_id: str | None = Field(
        default=None, description="Booking ID once confirmed, if available."
    )
    booking_reference: str | None = Field(
        default=None, description="Human-readable booking reference, if available."
    )
    availability_mode: AvailabilityMode | None = Field(
        default=None, description="Availability mode at last status update."
    )
    customer_message: str = Field(
        description="Safe customer-facing status description."
    )
    pending: bool = Field(
        description="True iff the operation is still in a non-terminal state."
    )
    terminal: bool = Field(
        description="True iff the operation has reached a terminal state."
    )
    updated_at: AwareDatetime = Field(
        description="Last status update time (offset-aware ISO-8601)."
    )
