using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IDeadLetterRepository"/> (spec 05 §5.3, T7).</summary>
public class DeadLetterRepository(ApplicationDbContext db) : IDeadLetterRepository
{
    /// <inheritdoc/>
    /// <remarks>
    /// Uses <c>INSERT … ON CONFLICT (tenant_id, message_id, consumer_name) DO NOTHING</c> so
    /// that a duplicate poison delivery (same message redelivered after a crash-before-ACK) does
    /// NOT throw a PK violation and does NOT cause an infinite redelivery loop.  Returns silently
    /// on duplicate — the first quarantine row wins.
    /// Enum column receives an explicit pg-enum cast; jsonb columns are passed as strings.
    /// </remarks>
    public async Task AddAsync(DeadLetterMessage message, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public.dead_letter_messages
                (tenant_id, message_id, consumer_name, message_type, payload,
                 error_class, attempt_history, first_failed_at, replay_state)
            VALUES
                ({message.TenantId}, {message.MessageId}, {message.ConsumerName},
                 {message.MessageType}, {message.Payload}::jsonb,
                 {message.ErrorClass}, {message.AttemptHistory}::jsonb,
                 {message.FirstFailedAt},
                 {PgEnum.Label(message.ReplayState)}::dlq_replay_state_enum)
            ON CONFLICT (tenant_id, message_id, consumer_name) DO NOTHING
            """,
            ct);
    }

    public Task<DeadLetterMessage?> GetAsync(
        string tenantId,
        string messageId,
        string consumerName,
        CancellationToken ct = default)
        => db.DeadLetterMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId
                  && e.MessageId == messageId
                  && e.ConsumerName == consumerName,
                ct);

    public Task<int> SetReplayStateAsync(
        string tenantId,
        string messageId,
        string consumerName,
        DlqReplayState state,
        CancellationToken ct = default)
        => db.DeadLetterMessages
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId
                     && e.MessageId == messageId
                     && e.ConsumerName == consumerName)
            .ExecuteUpdateAsync(
                b => b.SetProperty(e => e.ReplayState, state),
                ct);

    public Task<int> TryMarkReplayedAsync(
        string tenantId,
        string messageId,
        string consumerName,
        CancellationToken ct = default)
        // Conditional on replay_state = Quarantined: the WHERE clause is the atomic claim, so only
        // one of N concurrent replays affects a row (the rest get 0 rows → suppress duplicate effects).
        => db.DeadLetterMessages
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId
                     && e.MessageId == messageId
                     && e.ConsumerName == consumerName
                     && e.ReplayState == DlqReplayState.Quarantined)
            .ExecuteUpdateAsync(
                b => b.SetProperty(e => e.ReplayState, DlqReplayState.Replayed),
                ct);

    public Task<int> CountQuarantinedAsync(CancellationToken ct = default)
        => db.DeadLetterMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(e => e.ReplayState == DlqReplayState.Quarantined, ct);

    public async Task<double> OldestQuarantinedAgeSecondsAsync(CancellationToken ct = default)
    {
        var oldest = await db.DeadLetterMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(e => e.ReplayState == DlqReplayState.Quarantined)
            .MinAsync(e => (DateTimeOffset?)e.DeadLetteredAt, ct);

        return oldest.HasValue
            ? (DateTimeOffset.UtcNow - oldest.Value).TotalSeconds
            : 0d;
    }
}
