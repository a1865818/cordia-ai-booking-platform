using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Persistence port for the pre-order saga (spec 11 §5.3). All writes are tenant-scoped (Inv-13);
/// state advances are version-token + expected-state guarded so a concurrent worker or duplicate
/// delivery cannot double-advance (Inv-5/Inv-9).
/// </summary>
public interface IPreorderSagaRepository
{
    /// <summary>Inserts a new saga row (typically in <see cref="PreorderSagaState.Revalidating"/>).</summary>
    Task AddAsync(PreorderSaga saga, CancellationToken ct = default);

    /// <summary>Loads a saga by id within the current tenant scope, or <c>null</c> if absent.</summary>
    Task<PreorderSaga?> GetAsync(string sagaId, CancellationToken ct = default);

    /// <summary>Loads the saga owning <paramref name="operationId"/>, or <c>null</c> if none.</summary>
    Task<PreorderSaga?> GetByOperationAsync(string operationId, CancellationToken ct = default);

    /// <summary>
    /// Conditionally advances the saga <c>expectedState → next</c>, guarded by
    /// <paramref name="expectedVersion"/> AND the current state, bumping the version and stamping
    /// <paramref name="updatedAt"/> in one atomic UPDATE. Optionally persists the step checkpoints
    /// (<paramref name="setHoldId"/> / <paramref name="setDepositSessionId"/>) and/or a
    /// <paramref name="setFailureReason"/> and/or <paramref name="setStandalone"/> atomically with the
    /// advance — pass <c>null</c> to leave a field unchanged. Returns rows-affected: <c>1</c> = won the
    /// advance, <c>0</c> = lost the race / state already moved (reload + re-evaluate).
    /// </summary>
    Task<int> TryAdvanceStateAsync(
        string tenantId,
        string sagaId,
        long expectedVersion,
        PreorderSagaState expectedState,
        PreorderSagaState next,
        DateTimeOffset updatedAt,
        string? setHoldId = null,
        string? setDepositSessionId = null,
        string? setFailureReason = null,
        bool? setStandalone = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns non-terminal sagas whose <c>UpdatedAt</c> is older than <paramref name="olderThan"/>
    /// (across tenants) for the crash-recovery sweep — sagas stuck mid-step that need re-enqueuing
    /// from their persisted state (FM#8-11). Terminal sagas (Completed/FailedFinal/ManualReview)
    /// are excluded. Projects ONLY the identifiers the sweeper needs (the table grows with total
    /// pre-order volume and the sweep runs on a tight cadence) and is batch-capped — a still-stuck
    /// saga is simply picked up by the next sweep.
    /// </summary>
    Task<IReadOnlyList<StuckSagaRef>> FindStuckAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}

/// <summary>Identifier projection of a stuck saga returned by the crash-recovery sweep.</summary>
public sealed record StuckSagaRef(string TenantId, string SagaId, string OperationId);
