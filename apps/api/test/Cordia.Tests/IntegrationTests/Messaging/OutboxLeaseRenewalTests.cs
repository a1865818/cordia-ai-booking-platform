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
    /// The paired negative: renewal must not steal a row back. If another publisher reclaimed a row
    /// while it waited, that publisher owns it — this one skips it rather than publishing twice.
    /// </summary>
    [Fact]
    public async Task ARowReclaimedByAnotherPublisher_IsNotPublishedAgain_NorTakenBack()
    {
        var tenantId = $"lease-lost-{Guid.NewGuid():N}";
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var first = MakeMessage(tenantId, baseTime);
        var second = MakeMessage(tenantId, baseTime.AddSeconds(1));
        await SeedAsync(first, second);

        var transport = new ProbingTransport(fixture, TimeSpan.Zero)
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

    private OutboxPublisherHostedService BuildService(IMessageTransport transport, string tenantId)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => fixture.CreateContext("lease-worker"));
        services.AddTransient<IOutboxRepository, OutboxRepository>();
        services.AddTransient<IDeadLetterRepository, DeadLetterRepository>();
        services.AddTransient<IUnitOfWork, UnitOfWork>();
        services.AddSingleton(transport);
        services.ScopeOutboxClaimToTenant(tenantId);

        var sp = services.BuildServiceProvider();
        var options = new MessagingOptions();
        options.Publisher.LeaseSeconds = LeaseSeconds;
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
}
