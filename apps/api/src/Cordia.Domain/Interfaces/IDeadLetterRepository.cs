using Cordia.Domain.Entities;
using Cordia.Domain.Enums;

namespace Cordia.Domain.Interfaces;

/// <summary>Repository for dead-letter messages (spec 05 §5.3, §6.1, T7).</summary>
public interface IDeadLetterRepository
{
    /// <summary>Inserts a new dead-letter row (ReplayState must be set by the caller).</summary>
    Task AddAsync(DeadLetterMessage message, CancellationToken ct = default);

    /// <summary>
    /// Returns the dead-letter row for the given composite PK
    /// (tenant_id, message_id, consumer_name), or null if absent. Uses AsNoTracking.
    /// </summary>
    Task<DeadLetterMessage?> GetAsync(
        string tenantId,
        string messageId,
        string consumerName,
        CancellationToken ct = default);

    /// <summary>
    /// Updates <c>replay_state</c> on the specified row via ExecuteUpdate (no entity load).
    /// Uses IgnoreQueryFilters + explicit composite PK predicate.
    /// Returns the number of rows affected (0 = not found, 1 = updated).
    /// </summary>
    Task<int> SetReplayStateAsync(
        string tenantId,
        string messageId,
        string consumerName,
        DlqReplayState state,
        CancellationToken ct = default);

    /// <summary>
    /// Atomic state transition <c>Quarantined → Replayed</c>: a single conditional UPDATE guarded by
    /// <c>replay_state = 'quarantined'</c>. Returns rows-affected — exactly ONE caller wins the
    /// transition, so concurrent replays of the same row cannot both claim it. Returns 0 if the row
    /// is already Replayed/Discarded (lost the race) so the caller can suppress duplicate side effects
    /// (e.g. a second audit event).
    /// </summary>
    Task<int> TryMarkReplayedAsync(
        string tenantId,
        string messageId,
        string consumerName,
        CancellationToken ct = default);
}
