using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IPreorderSagaRepository"/> (spec 11 §5.3). Tenant scope is
/// enforced by the global query filter (Inv-13); state advances go through
/// <see cref="VersionTokenUpdate"/> for race-safe single-winner transitions (Inv-5/Inv-9).
/// </summary>
public class PreorderSagaRepository(ApplicationDbContext db) : IPreorderSagaRepository
{
    public async Task AddAsync(PreorderSaga saga, CancellationToken ct = default)
    {
        db.PreorderSagas.Add(saga);
        await db.SaveChangesAsync(ct);
    }

    public Task<PreorderSaga?> GetAsync(string sagaId, CancellationToken ct = default)
        => db.PreorderSagas.AsNoTracking().FirstOrDefaultAsync(s => s.SagaId == sagaId, ct);

    public Task<PreorderSaga?> GetByOperationAsync(string operationId, CancellationToken ct = default)
        => db.PreorderSagas.AsNoTracking().FirstOrDefaultAsync(s => s.OperationId == operationId, ct);

    public Task<int> TryAdvanceStateAsync(
        string tenantId,
        string sagaId,
        long expectedVersion,
        PreorderSagaState expectedState,
        PreorderSagaState next,
        DateTimeOffset updatedAt,
        string? setHoldId = null,
        string? setDepositSessionId = null,
        string? setFailureReason = null,
        CancellationToken ct = default)
        => VersionTokenUpdate.UpdateAsync<PreorderSaga>(
            db,
            expectedVersion,
            e => e.TenantId == tenantId && e.SagaId == sagaId && e.SagaState == expectedState,
            s =>
            {
                s.SetProperty(x => x.SagaState, next);
                s.SetProperty(x => x.UpdatedAt, updatedAt);
                if (setHoldId is not null)
                    s.SetProperty(x => x.HoldId, setHoldId);
                if (setDepositSessionId is not null)
                    s.SetProperty(x => x.DepositSessionId, setDepositSessionId);
                if (setFailureReason is not null)
                    s.SetProperty(x => x.FailureReason, setFailureReason);
            },
            ct);

    public async Task<IReadOnlyList<PreorderSaga>> FindStuckAsync(
        DateTimeOffset olderThan, CancellationToken ct = default)
        => await db.PreorderSagas
            .AsNoTracking()
            .IgnoreQueryFilters() // cross-tenant recovery sweep
            .Where(s => s.UpdatedAt < olderThan
                && s.SagaState != PreorderSagaState.Completed
                && s.SagaState != PreorderSagaState.FailedFinal
                && s.SagaState != PreorderSagaState.ManualReview)
            .OrderBy(s => s.UpdatedAt)
            .ToListAsync(ct);
}
