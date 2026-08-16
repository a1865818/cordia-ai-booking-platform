using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for human-handoff work items (spec 03 §4.3).</summary>
public interface IHandoffWorkItemRepository
{
    /// <summary>
    /// Returns the work item previously created under this dedup key, or null.
    /// AsNoTracking; scoped to the tenant explicitly (IgnoreQueryFilters).
    /// </summary>
    Task<HandoffWorkItem?> GetByIdempotencyKeyAsync(
        string tenantId, string idempotencyKey, CancellationToken ct = default);

    /// <summary>
    /// Atomic <c>INSERT … ON CONFLICT (tenant_id, handoff_idempotency_key) DO NOTHING</c>.
    /// Returns <see langword="true"/> when this call created the row, and
    /// <see langword="false"/> when an equivalent escalation already existed —
    /// which is a dedup hit, not an error (Inv-5/Inv-9).
    /// </summary>
    Task<bool> TryAddAsync(HandoffWorkItem workItem, CancellationToken ct = default);
}
