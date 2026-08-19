using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Restricts a test container's outbox <b>claim</b> to a single tenant.
///
/// <para><b>Why this exists.</b> <c>OutboxPublisherHostedService.PublishPendingBatchAsync</c> is
/// deliberately CROSS-TENANT (spec §5.1): <c>OutboxRepository.ClaimBatchAsync</c> issues
/// <c>WHERE published_at IS NULL ...</c> with no tenant predicate, because in production one worker
/// drains every tenant. Tests that drive a publisher tick through a container holding a FAKE
/// <c>IBookingProviderAdapter</c> therefore dispatch <i>every</i> unpublished row in the shared test
/// database — including rows a test in another xUnit collection wrote moments earlier.</para>
///
/// <para>The whole integration suite runs against one database, and xUnit runs collections in
/// parallel, so the <c>AcceptanceApi</c> collection is a live producer of unpublished
/// <c>booking.command.requested.v1</c> rows while the <c>Postgres</c> collection is running. A
/// foreign row claimed by a test's tick calls that test's fake adapter — inflating
/// <c>CreateCallCount</c>/<c>CancelCallCount</c> — and writes the fake's provider reference onto
/// another tenant's operation. That is issue #90: it is a genuine concurrency race on a
/// deliberately cross-tenant queue, not an ordering dependency, which is why the failure alternated
/// between classes and vanished in isolation.</para>
///
/// <para>Marking the pre-existing backlog published (the <c>NeutralizeExistingUnpublished</c>
/// helpers) cannot close this: it only covers rows that exist at that instant, never rows a
/// concurrent writer inserts before the tick. Bounding the CLAIM to the test's own tenant does,
/// and it leaves every other phase of the real publisher path (route, transport, consumer, mark)
/// untouched. Cross-tenant claiming itself is still pinned directly against the repository by
/// <c>OutboxPublisherTests.ClaimBatch_SetsLease_AndIsCrossTenant</c>.</para>
/// </summary>
internal static class OutboxTenantScope
{
    /// <summary>
    /// Re-registers <see cref="IOutboxRepository"/> so the publisher's claim phase only ever sees
    /// <paramref name="tenantId"/>'s rows. Call AFTER <c>AddInfrastructure</c> — the last
    /// registration is the one resolved.
    /// </summary>
    public static IServiceCollection ScopeOutboxClaimToTenant(
        this IServiceCollection services, string tenantId)
    {
        services.AddScoped<IOutboxRepository>(sp =>
        {
            var db = sp.GetRequiredService<ApplicationDbContext>();
            return new TenantScopedOutboxRepository(db, new OutboxRepository(db), tenantId);
        });

        return services;
    }
}

/// <summary>
/// Delegating <see cref="IOutboxRepository"/> whose <see cref="ClaimBatchAsync"/> adds a
/// <c>tenant_id</c> predicate to the production claim. Every other member is the real repository.
/// </summary>
internal sealed class TenantScopedOutboxRepository(
    ApplicationDbContext db,
    IOutboxRepository inner,
    string tenantId) : IOutboxRepository
{
    public Task AddAsync(OutboxMessage message, CancellationToken ct = default)
        => inner.AddAsync(message, ct);

    public Task AddRangeAsync(IEnumerable<OutboxMessage> messages, CancellationToken ct = default)
        => inner.AddRangeAsync(messages, ct);

    public Task<IReadOnlyList<OutboxMessage>> ClaimUnpublishedAsync(
        string requestedTenantId, int limit, CancellationToken ct = default)
        => inner.ClaimUnpublishedAsync(requestedTenantId, limit, ct);

    public Task MarkPublishedAsync(
        string messageTenantId, string messageId, DateTimeOffset publishedAt, CancellationToken ct = default)
        => inner.MarkPublishedAsync(messageTenantId, messageId, publishedAt, ct);

    public Task<bool> ExistsAsync(string requestedTenantId, string messageId, CancellationToken ct = default)
        => inner.ExistsAsync(requestedTenantId, messageId, ct);

    /// <summary>
    /// The production claim (<c>OutboxRepository.ClaimBatchAsync</c>) with one extra predicate:
    /// <c>tenant_id = {0}</c>. Same short transaction, same lease/attempt bookkeeping, same
    /// <c>FOR UPDATE SKIP LOCKED</c> — so a foreign row is neither dispatched nor even leased.
    /// </summary>
    public async Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(
        int limit,
        string leaseOwner,
        DateTimeOffset leaseExpiresAt,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var rows = await db.Set<OutboxMessage>()
            .FromSqlRaw(
                "SELECT * FROM public.outbox_messages " +
                "WHERE tenant_id = {0} AND published_at IS NULL " +
                "AND (lease_owner IS NULL OR lease_expires_at < {1}) " +
                "ORDER BY occurred_at " +
                "LIMIT {2} " +
                "FOR UPDATE SKIP LOCKED",
                tenantId,
                now,
                limit)
            .IgnoreQueryFilters()
            .ToListAsync(ct);

        foreach (var row in rows)
        {
            row.LeaseOwner = leaseOwner;
            row.LeaseExpiresAt = leaseExpiresAt;
            row.PublishAttempts++;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return rows;
    }
}
