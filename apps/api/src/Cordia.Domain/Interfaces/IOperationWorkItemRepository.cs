using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for operation work items; version-token updates use the §22.1 helper.</summary>
public interface IOperationWorkItemRepository
{
    // NOTE: there is deliberately no plain `AddAsync`.
    //
    // Every writer here is racing another worker for the same operation, and the partial unique
    // index (uq_open_work_item_per_operation) makes a losing INSERT raise 23505 — which aborts the
    // WHOLE ambient transaction, taking the status transition, outbox row and audit event down with
    // it. All three creation paths used read-then-add and were all exposed to that. Offering an
    // unguarded insert on this interface is offering that bug to the next caller, so the only way to
    // create a work item is TryAddOpenAsync below.

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
    /// Returns the open work items for ONE tenant, oldest SLA deadline first — the queue an
    /// operator actually works through.
    ///
    /// <para>Until this existed the table could only be counted, never read: the two aggregate
    /// methods below are cross-tenant and feed metrics gauges, and
    /// <see cref="GetOpenByOperationAsync"/> needs the operation id you are already looking for. So
    /// an operation could be escalated to ManualReview with a durable work item carrying a reason
    /// and an SLA deadline, and there was no way to ask "what is outstanding?" — the escalation was
    /// recorded and then unreachable. A dashboard number told someone the depth was rising without
    /// telling them what was in it.</para>
    ///
    /// <para>Tenant-scoped by an explicit predicate (Inv-13). Ordered by
    /// <see cref="OperationWorkItem.SlaDeadline"/> so the most overdue item is first, which is the
    /// order the work should be done in — not creation order, because reasons carry different
    /// deadlines.</para>
    /// </summary>
    Task<IReadOnlyList<OperationWorkItem>> ListOpenAsync(
        string tenantId, int limit, CancellationToken ct = default);

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
