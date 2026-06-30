using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for operation work items; version-token updates use the §22.1 helper.</summary>
public interface IOperationWorkItemRepository
{
    Task AddAsync(OperationWorkItem workItem, CancellationToken ct = default);

    /// <summary>
    /// Conditional version-token UPDATE (§6.2): sets <paramref name="nextState"/> and optional
    /// assignee only when the row is in <paramref name="expectedState"/> AND at
    /// <paramref name="expectedVersion"/>. Returns rows-affected (1 = won race, 0 = lost race /
    /// wrong state → reload).
    /// </summary>
    Task<int> TryUpdateStateAsync(
        string tenantId,
        string workItemId,
        long expectedVersion,
        string expectedState,
        string nextState,
        string? assignee,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the first open (State=="open") work item for the given operation, or null if none.
    /// AsNoTracking; cross-tenant safe (IgnoreQueryFilters).
    /// </summary>
    Task<OperationWorkItem?> GetOpenByOperationAsync(
        string tenantId, string operationId, CancellationToken ct = default);

    /// <summary>
    /// Atomic <c>INSERT … ON CONFLICT (tenant_id, operation_id) WHERE state = 'open' DO NOTHING</c>
    /// (mirrors <c>InboxRepository.TryAdmitAsync</c>). Returns <see langword="true"/> when the row
    /// was inserted (rows == 1); returns <see langword="false"/> when another worker already inserted
    /// an open work item for the same operation (dedup hit — benign no-op).
    /// </summary>
    Task<bool> TryAddOpenAsync(OperationWorkItem workItem, CancellationToken ct = default);

    /// <summary>
    /// Returns the count of open work items (State == "open") across all tenants.
    /// Used for the <c>fulfillment.manual_review.depth</c> gauge.
    /// </summary>
    Task<int> CountOpenAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the age in seconds of the oldest open work item (based on <c>CreatedAt</c>),
    /// or 0 when there are none.
    /// </summary>
    Task<double> OldestOpenAgeSecondsAsync(CancellationToken ct = default);
}
