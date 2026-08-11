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
                 {PgEnum.Label(record.OperationType)}::operation_type_enum,
                 {record.RequestHash}, {record.OperationId},
                 {PgEnum.Label(record.ProcessingStatus)}::idempotency_status_enum,
                 {record.ResponseSnapshot}::jsonb,
                 {record.CreatedAt}, {record.LastSeenAt}, {record.RetentionDeadline})
            ON CONFLICT (tenant_id, idempotency_key) DO NOTHING
            """,
            ct);

        return rows == 1;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Conditional on <c>operation_id</c> so a concurrent caller cannot delete a record another
    /// transaction has already re-bound to a fresh operation. Tenant is filtered explicitly as
    /// well as by the global query filter, matching <see cref="GetByKeyAsync"/>.
    /// </remarks>
    public Task<int> TryVoidAsync(
        string tenantId,
        string idempotencyKey,
        string expectedOperationId,
        CancellationToken ct = default)
        => db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            DELETE FROM public.idempotency_records
            WHERE tenant_id = {tenantId}
              AND idempotency_key = {idempotencyKey}
              AND operation_id = {expectedOperationId}
            """,
            ct);
}
