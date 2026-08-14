using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for the <see cref="DepositSession"/> aggregate.</summary>
public interface IDepositSessionRepository
{
    Task AddAsync(DepositSession session, CancellationToken ct = default);

    Task<DepositSession?> GetAsync(string tenantId, string depositSessionId, CancellationToken ct = default);

    /// <summary>
    /// Returns the most-recently created deposit session for <paramref name="operationId"/>
    /// (or <see langword="null"/> if none exists). Used by deposit event consumers to resolve
    /// the session without knowing its ID upfront.
    /// </summary>
    Task<DepositSession?> GetByOperationAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default);

    /// <summary>
    /// Version-token conditional transition (§6.2): sets <paramref name="next"/> status only when
    /// the row is in <paramref name="expectedStatus"/> AND at <paramref name="expectedVersion"/>.
    /// Returns rows-affected (1 = success, 0 = lost race / wrong state → caller should reload).
    /// Optionally sets <paramref name="paidAt"/> when transitioning to Paid.
    /// </summary>
    Task<int> TryTransitionStatusAsync(
        string tenantId,
        string depositSessionId,
        long expectedVersion,
        DepositStatus expectedStatus,
        DepositStatus next,
        DateTimeOffset? paidAt = null,
        CancellationToken ct = default);

    /// <summary>
    /// Recent deposit sessions for the tenant, newest <c>CreatedAt</c> first.
    /// Optional <paramref name="status"/>, <paramref name="from"/>, and <paramref name="to"/>
    /// filter on status and created-at range.
    /// </summary>
    Task<IReadOnlyList<DepositSession>> ListAsync(
        string tenantId,
        DepositStatus? status,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int limit,
        CancellationToken ct = default);
}
