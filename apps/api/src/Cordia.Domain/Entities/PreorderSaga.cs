using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;

namespace Cordia.Domain.Entities;

/// <summary>
/// Saga orchestrator row for the preorder flow. PK (tenant_id, saga_id).
/// FK (tenant_id, operation_id) → booking_operations ON DELETE RESTRICT.
/// PreorderReference is stored as jsonb (not null).
/// Optimistic-concurrency version token (§22.1).
/// </summary>
public class PreorderSaga : ITenantScoped
{
    public string TenantId { get; set; } = string.Empty;

    public string SagaId { get; set; } = string.Empty;

    public string OperationId { get; set; } = string.Empty;

    public PreorderSagaState SagaState { get; set; }

    /// <summary>jsonb — preorder reference payload; not null. Defaults to a valid empty
    /// JSON object — an empty string is not valid jsonb and would fail on insert.</summary>
    public string PreorderReference { get; set; } = "{}";

    public string? HoldId { get; set; }

    public string? DepositSessionId { get; set; }

    /// <summary>
    /// Booking slot the capacity hold is taken against (from the confirmed booking request); null for
    /// a standalone order. Persisted so the hold step + crash-recovery resume can reserve without the
    /// slot being re-supplied on every message.
    /// </summary>
    public string? SlotId { get; set; }

    /// <summary>Seats/covers to hold for the booking; 0 for a standalone order.</summary>
    public int RequestedCapacity { get; set; }

    /// <summary>
    /// True for a standalone order (no booking, §21.4): the saga skips the capacity-hold and
    /// booking-commit steps. False (default) for a booking-attached pre-order.
    /// </summary>
    public bool IsStandalone { get; set; }

    /// <summary>
    /// Coarse machine reason recorded when the saga reaches FailedFinal / ManualReview (compensation
    /// or an unconfirmable external undo); null while healthy. For audit + outcome context — never
    /// card data or PII (Inv-12).
    /// </summary>
    public string? FailureReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }
}
