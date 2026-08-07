using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
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

    public Task<VerificationGrant?> FindGrantByIdIgnoringConsumptionAsync(
        string tenantId,
        string grantId,
        string customerRef,
        VerificationActionEnum action,
        string? targetBookingId,
        DateTimeOffset now,
        CancellationToken ct = default)
        // SCOPE only — tenant, customer, action, target. Deliberately not liveness: neither
        // consumption nor expiry is checked here.
        //
        // Both are enforced atomically by ConsumeAsync, which is where a NEW operation is actually
        // fenced. Re-checking them in this pre-check only breaks the caller's own honest RETRY:
        // the first attempt spends the grant and starts its clock running out, so a redelivery
        // after the TTL — of a request that was already accepted, whose response was simply lost —
        // was answered with a 403 instead of the recorded operation. Expiry bounds how long a grant
        // can AUTHORIZE something new, not how long the platform will admit that it already did.
        => db.VerificationGrants
            .AsNoTracking()
            .Where(g =>
                g.TenantId == tenantId
                && g.GrantId == grantId
                && g.CustomerRef == customerRef
                && g.AllowedAction == action
                && (g.TargetBookingId == null || g.TargetBookingId == targetBookingId))
            .FirstOrDefaultAsync(ct);

    public async Task<VerificationLevel?> GetAssuranceLevelAsync(
        string tenantId,
        string grantId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // Expiry matters, consumption does not: a grant that has been spent still records how
        // well the party was verified at the time they confirmed. An EXPIRED one does not — the
        // assurance it attests to has lapsed.
        return await db.VerificationGrants
            .AsNoTracking()
            .Where(g => g.TenantId == tenantId && g.GrantId == grantId && g.ExpiresAt > now)
            .Select(g => (VerificationLevel?)g.VerificationLevel)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<VerificationLevel?> GetAssuranceLevelForCustomerAsync(
        string tenantId,
        string grantId,
        string customerRef,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // Same consumption stance as GetAssuranceLevelAsync — a spent grant still attests to how
        // well the party was verified — plus the customer binding the unscoped read cannot make.
        return await db.VerificationGrants
            .AsNoTracking()
            .Where(g => g.TenantId == tenantId
                        && g.GrantId == grantId
                        && g.CustomerRef == customerRef
                        && g.ExpiresAt > now)
            .Select(g => (VerificationLevel?)g.VerificationLevel)
            .FirstOrDefaultAsync(ct);
    }

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
