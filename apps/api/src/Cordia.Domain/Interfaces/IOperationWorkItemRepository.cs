using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for operation work items; version-token updates use the §22.1 helper.</summary>
public interface IOperationWorkItemRepository
{
    Task AddAsync(OperationWorkItem workItem, CancellationToken ct = default);

    /// <summary>
    /// Conditional version-token UPDATE: sets state and optional assignee.
    /// Returns rows-affected (1 = won race, 0 = lost race → reload).
    /// </summary>
    Task<int> TryUpdateStateAsync(
        string tenantId,
        string workItemId,
        long expectedVersion,
        string nextState,
        string? assignee,
        CancellationToken ct = default);
}
