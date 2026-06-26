using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for the <see cref="DepositSession"/> aggregate.</summary>
public interface IDepositSessionRepository
{
    Task AddAsync(DepositSession session, CancellationToken ct = default);

    Task<DepositSession?> GetAsync(string tenantId, string depositSessionId, CancellationToken ct = default);

    /// <summary>
    /// Version-token conditional transition (§6.2): sets <paramref name="next"/> status only when
    /// the row is in <paramref name="expectedStatus"/> AND at <paramref name="expectedVersion"/>.
    /// Returns rows-affected (1 = success, 0 = lost race / wrong state → caller should reload).
    /// </summary>
    Task<int> TryTransitionStatusAsync(
        string tenantId,
        string depositSessionId,
        long expectedVersion,
        DepositStatus expectedStatus,
        DepositStatus next,
        CancellationToken ct = default);
}
