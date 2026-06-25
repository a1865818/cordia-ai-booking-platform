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

    /// <summary>jsonb — preorder reference payload; not null.</summary>
    public string PreorderReference { get; set; } = string.Empty;

    public string? HoldId { get; set; }

    public string? DepositSessionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic-concurrency token (§22.1); default 0.</summary>
    public long Version { get; set; }
}
