using Cordia.Domain.Entities;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IVerificationGrantRepository"/>.</summary>
public class VerificationGrantRepository(ApplicationDbContext db) : IVerificationGrantRepository
{
    public async Task AddAsync(VerificationGrant grant, CancellationToken ct = default)
    {
        db.VerificationGrants.Add(grant);
        await db.SaveChangesAsync(ct);
    }

    public Task<VerificationGrant?> FindValidGrantAsync(
        string tenantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        DateTimeOffset now,
        CancellationToken ct = default)
        => db.VerificationGrants
            .AsNoTracking()
            .Where(g =>
                g.TenantId == tenantId
                && g.CustomerRef == customerRef
                && g.AllowedAction == action
                && (g.TargetBookingId == null || g.TargetBookingId == targetBookingId)
                && now < g.ExpiresAt
                && g.ConsumedAt == null)
            .FirstOrDefaultAsync(ct);

    public Task<VerificationGrant?> FindValidGrantByIdAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        DateTimeOffset now,
        CancellationToken ct = default)
        => db.VerificationGrants
            .AsNoTracking()
            .Where(g =>
                g.TenantId == tenantId
                && g.GrantId == grantId
                && g.CustomerRef == customerRef
                && g.AllowedAction == action
                && (g.TargetBookingId == null || g.TargetBookingId == targetBookingId)
                && now < g.ExpiresAt
                && g.ConsumedAt == null)
            .FirstOrDefaultAsync(ct);

    public Task<int> ConsumeAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        DateTimeOffset consumedAt,
        CancellationToken ct = default)
        => db.VerificationGrants
            .IgnoreQueryFilters()
            // Reject expired grants atomically (§9.3 now < expires_at), closing the TOCTOU
            // window between FindValidGrantAsync and consumption.
            .Where(g => g.TenantId == tenantId
                && g.GrantId == grantId
                && g.CustomerRef == customerRef
                && g.AllowedAction == action
                && (g.TargetBookingId == null || g.TargetBookingId == targetBookingId)
                && g.ConsumedAt == null
                && consumedAt < g.ExpiresAt)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.ConsumedAt, consumedAt),
                ct);
}
