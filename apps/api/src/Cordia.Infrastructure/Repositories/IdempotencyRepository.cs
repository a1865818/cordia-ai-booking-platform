using System.Reflection;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IIdempotencyRepository"/>.</summary>
public class IdempotencyRepository(ApplicationDbContext db) : IIdempotencyRepository
{
    public async Task AddAsync(IdempotencyRecord record, CancellationToken ct = default)
    {
        db.IdempotencyRecords.Add(record);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Filters explicitly on both <paramref name="tenantId"/> and <paramref name="idempotencyKey"/>
    /// in addition to the ambient global query filter (belt-and-suspenders tenant isolation).
    /// </remarks>
    public Task<IdempotencyRecord?> GetByKeyAsync(string tenantId, string idempotencyKey, CancellationToken ct = default)
        => db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.IdempotencyKey == idempotencyKey, ct);

    /// <inheritdoc/>
    /// <remarks>
    /// Uses <c>INSERT … ON CONFLICT (tenant_id, idempotency_key) DO NOTHING</c> so that
    /// a losing concurrent writer does NOT abort the surrounding transaction (unlike a
    /// bare unique violation). All enum columns receive explicit pg-enum casts to avoid
    /// "unknown type" errors when the Npgsql type-mapper has not yet seen the enum on
    /// this connection. <c>response_snapshot</c> is cast to <c>jsonb</c>.
    /// Returns 1 (won) or 0 (lost).
    /// </remarks>
    public async Task<bool> ElectCreatorAsync(IdempotencyRecord record, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO public.idempotency_records
                (tenant_id, idempotency_key, operation_type, request_hash, operation_id,
                 processing_status, response_snapshot, created_at, last_seen_at, retention_deadline)
            VALUES
                ({record.TenantId}, {record.IdempotencyKey},
                 {PgLabel(record.OperationType)}::operation_type_enum,
                 {record.RequestHash}, {record.OperationId},
                 {PgLabel(record.ProcessingStatus)}::idempotency_status_enum,
                 {record.ResponseSnapshot}::jsonb,
                 {record.CreatedAt}, {record.LastSeenAt}, {record.RetentionDeadline})
            ON CONFLICT (tenant_id, idempotency_key) DO NOTHING
            """,
            ct);

        return rows == 1;
    }

    /// <summary>
    /// Returns the authoritative PostgreSQL enum label from the <c>[PgName]</c> attribute
    /// on the enum member. Falls back to <c>ToString().ToLowerInvariant()</c> only when
    /// the attribute is absent, so multi-word names like <c>InProgress</c> → <c>"in_progress"</c>
    /// are always sourced from the declared <c>[PgName("in_progress")]</c> rather than
    /// a naive lower-case transform.
    /// </summary>
    private static string PgLabel<TEnum>(TEnum value) where TEnum : struct, Enum
        => typeof(TEnum).GetField(value.ToString())!
            .GetCustomAttribute<NpgsqlTypes.PgNameAttribute>()?.PgName
            ?? value.ToString().ToLowerInvariant();
}
