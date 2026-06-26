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
}
