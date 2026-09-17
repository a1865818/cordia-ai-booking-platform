#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Cordia.Infrastructure.Messaging;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Outbox lease renewal within a lane (#102, spec 05 OD-3).
///
/// <para>The claim stamps one lease on the whole batch and rows in a lane publish one after
/// another, so without renewal the third slow row in a lane began after its lease had expired —
/// open to reclaim by another publisher while its first owner was about to publish it. These tests
/// shrink the lease to two seconds and the per-row publish to 1.5, so the third row starts at ~3 s:
/// past an unrenewed lease, inside a renewed one.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class OutboxLeaseRenewalTests(PostgresFixture fixture)
{
    private const string BookingType = "booking.command.requested.v1";
    private const int LeaseSeconds = 2;
    private static readonly TimeSpan SlowPublish = TimeSpan.FromMilliseconds(1500);

    [Fact]
    public async Task EveryRowInASlowLane_StaysLeasedWhileItWaitsItsTurn()
    {
        var tenantId = $"lease-renew-{Guid.NewGuid():N}";
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var rows = Enumerable.Range(0, 3).Select(i => MakeMessage(tenantId, baseTime.AddSeconds(i))).ToArray();
        await SeedAsync(rows);

        var transport = new ProbingTransport(fixture, SlowPublish);
        await BuildService(transport, tenantId).PublishPendingBatchAsync(CancellationToken.None);

        Assert.Equal(rows.Select(r => r.MessageId), transport.Published);
        Assert.True(
            transport.UnleasedWhileWaiting.Count == 0,
            $"Rows sat in the lane with an expired lease — open to reclaim by another publisher: "
            + $"{string.Join(", ", transport.UnleasedWhileWaiting)}. The lane is running on the "
            + "claim-time lease instead of renewing it (#102).");
    }

    /// <summary>
    /// The paired negative: renewal must not steal a row back. A publisher can reclaim a row only
    /// after its lease expires, so the first row here outlasts half the lease, the second row's lease
    /// is taken by another worker meanwhile, and the heartbeat at the second row must see that its
    /// renewal matched nothing and step aside rather than publish it twice.
    /// </summary>
    [Fact]
    public async Task ARowReclaimedByAnotherPublisher_IsNotPublishedAgain_NorTakenBack()
    {
        var tenantId = $"lease-lost-{Guid.NewGuid():N}";
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var first = MakeMessage(tenantId, baseTime);
        var second = MakeMessage(tenantId, baseTime.AddSeconds(1));
        await SeedAsync(first, second);

        var transport = new ProbingTransport(fixture, TimeSpan.FromMilliseconds(1100))
        {
            // While the first row is in flight, another worker takes the second.
            OnPublish = async messageId =>
            {
                if (messageId != first.MessageId) return;
                await using var ctx = fixture.CreateContext(tenantId);
                await ctx.OutboxMessages.IgnoreQueryFilters()
                    .Where(m => m.MessageId == second.MessageId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(m => m.LeaseOwner, "outbox-pub-other-worker")
                        .SetProperty(m => m.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(5)));
            },
        };

        await BuildService(transport, tenantId).PublishPendingBatchAsync(CancellationToken.None);

        Assert.Equal([first.MessageId], transport.Published);

        await using var check = fixture.CreateContext(tenantId);
        var reclaimed = await check.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(m => m.MessageId == second.MessageId);
        Assert.Equal("outbox-pub-other-worker", reclaimed.LeaseOwner);
        Assert.Null(reclaimed.PublishedAt);
    }

    /// <summary>
    /// A renewal is bookkeeping. If the database refuses the current row's renewal, that row is left
    /// for lease-expiry reclaim — the tick must not fault and strand the rest of the lane. (A zero
    /// lease makes the heartbeat renew at every row, so the failure is actually reached.)
    /// </summary>
    [Fact]
    public async Task ARenewalThatFails_SkipsOnlyItsRow_AndTheLaneCarriesOn()
    {
        var tenantId = $"lease-renew-fail-{Guid.NewGuid():N}";
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var first = MakeMessage(tenantId, baseTime);
        var second = MakeMessage(tenantId, baseTime.AddSeconds(1));
        await SeedAsync(first, second);

        var transport = new ProbingTransport(fixture, TimeSpan.Zero);
        var spy = new RenewalSpy { FailWhenRenewing = first.MessageId };

        await BuildService(transport, tenantId, spy, leaseSeconds: 0).PublishPendingBatchAsync(CancellationToken.None);

        Assert.Equal([second.MessageId], transport.Published);
    }

    /// <summary>
    /// When renewing the rows QUEUED behind fails after the current row's own renewal succeeded, the
    /// current row is still published: only the rows whose renewal failed are deferred.
    /// </summary>
    [Fact]
    public async Task AFailedRenewalOfQueuedRows_DoesNotSkipTheCurrentRow()
    {
        var tenantId = $"lease-renew-tail-{Guid.NewGuid():N}";
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var first = MakeMessage(tenantId, baseTime);
        var second = MakeMessage(tenantId, baseTime.AddSeconds(1));
        await SeedAsync(first, second);

        var transport = new ProbingTransport(fixture, TimeSpan.Zero);
        // Every renewal that includes the second row fails: first as the queued tail behind the
        // first row, then as the current row itself.
        var spy = new RenewalSpy { FailWhenRenewing = second.MessageId };

        await BuildService(transport, tenantId, spy, leaseSeconds: 0).PublishPendingBatchAsync(CancellationToken.None);

        Assert.Equal([first.MessageId], transport.Published);
    }

    /// <summary>
    /// The heartbeat bounds write load. A lane that finishes well inside half its lease renews
    /// nothing — renewing every waiting row before every publish cost a full lane ~2 UPDATEs per row.
    /// </summary>
    [Fact]
    public async Task AFastLane_WritesNoRenewals()
    {
        var tenantId = $"lease-renew-fast-{Guid.NewGuid():N}";
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var rows = Enumerable.Range(0, 10).Select(i => MakeMessage(tenantId, baseTime.AddSeconds(i))).ToArray();
        await SeedAsync(rows);

        var transport = new ProbingTransport(fixture, TimeSpan.Zero);
        var spy = new RenewalSpy();

        await BuildService(transport, tenantId, spy, leaseSeconds: 300).PublishPendingBatchAsync(CancellationToken.None);

        Assert.Equal(rows.Select(r => r.MessageId), transport.Published);
        Assert.Equal(0, spy.Calls);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static OutboxMessage MakeMessage(string tenantId, DateTimeOffset occurredAt) =>
        new()
        {
            TenantId = tenantId,
            MessageId = $"msg-{Guid.NewGuid():N}",
            MessageType = BookingType,
            CorrelationId = $"corr-{Guid.NewGuid():N}",
            Payload = "{}",
            OccurredAt = occurredAt,
        };

    private async Task SeedAsync(params OutboxMessage[] messages)
    {
        await using var ctx = fixture.CreateContext(messages[0].TenantId);
        ctx.OutboxMessages.AddRange(messages);
        await ctx.SaveChangesAsync();
    }

    private OutboxPublisherHostedService BuildService(
        IMessageTransport transport, string tenantId, RenewalSpy? spy = null, int leaseSeconds = LeaseSeconds)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateContext("lease-worker"));
        services.AddTransient<IOutboxRepository, OutboxRepository>();
        services.AddTransient<IDeadLetterRepository, DeadLetterRepository>();
        services.AddTransient<IUnitOfWork, UnitOfWork>();
        services.AddSingleton(transport);
        services.ScopeOutboxClaimToTenant(tenantId);
        if (spy is not null)
        {
            // Decorates whatever the scope resolves, keeping the tenant-scoped claim above.
            var scoped = services.Last(d => d.ServiceType == typeof(IOutboxRepository));
            services.AddScoped<IOutboxRepository>(provider => new SpyingRenewalRepository(
                (IOutboxRepository)(scoped.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, scoped.ImplementationType!)),
                spy));
        }

        var sp = services.BuildServiceProvider();
        var options = new MessagingOptions();
        options.Publisher.LeaseSeconds = leaseSeconds;
        return new OutboxPublisherHostedService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<OutboxPublisherHostedService>.Instance);
    }

    /// <summary>
    /// Takes <c>delay</c> to "publish" each row, then — at the latest moment of that publish —
    /// records every row of the tenant that is still unpublished but no longer leased. The row in
    /// flight and every row queued behind it must all still be covered at that point.
    /// </summary>
    private sealed class ProbingTransport(PostgresFixture fixture, TimeSpan delay) : IMessageTransport
    {
        public List<string> Published { get; } = [];
        public List<string> UnleasedWhileWaiting { get; } = [];
        public Func<string, Task>? OnPublish { get; init; }

        public async Task PublishAsync(JobEnvelope envelope, LogicalQueue queue, CancellationToken ct = default)
        {
            Published.Add(envelope.MessageId);
            if (OnPublish is not null)
                await OnPublish(envelope.MessageId);
            await Task.Delay(delay, ct);

            await using var ctx = fixture.CreateContext(envelope.TenantId);
            var now = DateTimeOffset.UtcNow;
            UnleasedWhileWaiting.AddRange(await ctx.OutboxMessages.IgnoreQueryFilters().AsNoTracking()
                .Where(m => m.TenantId == envelope.TenantId
                    && m.PublishedAt == null
                    && !(m.LeaseExpiresAt > now))
                .Select(m => m.MessageId)
                .ToListAsync(ct));
        }
    }

    /// <summary>Counts renewal calls, and optionally fails any renewal that includes one message.</summary>
    private sealed class RenewalSpy
    {
        public int Calls;
        public string? FailWhenRenewing { get; init; }
    }

    private sealed class SpyingRenewalRepository(IOutboxRepository inner, RenewalSpy spy) : IOutboxRepository
    {
        public Task AddAsync(OutboxMessage message, CancellationToken ct = default) => inner.AddAsync(message, ct);
        public Task AddRangeAsync(IEnumerable<OutboxMessage> messages, CancellationToken ct = default) => inner.AddRangeAsync(messages, ct);
        public Task<IReadOnlyList<OutboxMessage>> ClaimUnpublishedAsync(string tenantId, int limit, CancellationToken ct = default)
            => inner.ClaimUnpublishedAsync(tenantId, limit, ct);
        public Task<IReadOnlyList<OutboxMessage>> ClaimBatchAsync(int limit, string leaseOwner, DateTimeOffset leaseExpiresAt, DateTimeOffset now, CancellationToken ct = default)
            => inner.ClaimBatchAsync(limit, leaseOwner, leaseExpiresAt, now, ct);
        public Task<int> RenewLeaseAsync(IReadOnlyCollection<string> messageIds, string leaseOwner, DateTimeOffset leaseExpiresAt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref spy.Calls);
            return spy.FailWhenRenewing is not null && messageIds.Contains(spy.FailWhenRenewing)
                ? throw new TimeoutException("simulated database timeout during renewal")
                : inner.RenewLeaseAsync(messageIds, leaseOwner, leaseExpiresAt, ct);
        }
        public Task MarkPublishedAsync(string tenantId, string id, DateTimeOffset publishedAt, CancellationToken ct = default)
            => inner.MarkPublishedAsync(tenantId, id, publishedAt, ct);
        public Task<bool> ExistsAsync(string tenantId, string id, CancellationToken ct = default) => inner.ExistsAsync(tenantId, id, ct);
    }
}
