#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Cordia.Infrastructure.Messaging;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Pins the per-lane dispatch behaviour of <see cref="OutboxPublisherHostedService"/> (#96).
///
/// <para><b>What broke.</b> The publish phase was one sequential <c>foreach</c> over the whole
/// claimed batch, so a single slow message stalled every message claimed with it — regardless of
/// which logical queue they belonged to. Since PR #88 replaced the stub extractor with the real
/// agent, <c>menu.commands</c> carries a call bounded by
/// <c>LangGraphOptions.ExtractTimeoutSeconds</c> (120 s), so a menu ingestion could hold every
/// <c>booking.commands</c> and <c>notification.commands</c> row in the same batch behind it. Spec 05
/// never names or budgets for head-of-line blocking across queues.</para>
///
/// <para><b>What must NOT change with it.</b> <see cref="LogicalQueueMetadata.OrderingRequired"/> is
/// <c>true</c> for <c>booking.commands</c> and <c>booking.reconcile</c>. Concurrency is therefore
/// across lanes only — never within one. Test 2 is the pin for that half, and it is the reason the
/// fix groups by queue rather than simply parallelising the batch.</para>
///
/// <para>Both tests scope the claim to their own tenant via
/// <see cref="OutboxTenantScope.ScopeOutboxClaimToTenant"/>: the production claim is deliberately
/// cross-tenant, and without the scope a concurrently-running collection's rows land in this
/// test's batch and its lane assertions measure someone else's traffic.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class OutboxLaneTests(PostgresFixture fixture)
{
    private const string BookingType = "booking.command.requested.v1";
    private const string MenuType = "menu.commands.ingest.v1";

    /// <summary>How long the slow lane waits for the fast lane before declaring the fix absent.</summary>
    private static readonly TimeSpan CrossLaneBudget = TimeSpan.FromSeconds(10);

    // ── Test 1: a slow lane must not hold up a different lane ───────────────────────

    /// <summary>
    /// The head-of-line pin. The slow (<c>menu.commands</c>) row is stamped OLDER than the fast
    /// (<c>booking.commands</c>) row, so the claim's <c>ORDER BY occurred_at</c> puts it first —
    /// exactly the case the old sequential loop mishandled.
    ///
    /// <para>The slow lane blocks until the fast lane has published. Under lane concurrency that
    /// resolves immediately. Under the old sequential dispatch it cannot resolve at all, because
    /// the fast row is not reached until the slow one returns — so the wait times out and
    /// <c>SlowSawFastFirst</c> is false. The assertion is on that flag rather than on wall-clock,
    /// so the test states the property instead of measuring a race.</para>
    /// </summary>
    [Fact]
    public async Task SlowLane_DoesNotBlock_ADifferentLane()
    {
        var tenantId = $"lane-hol-{Guid.NewGuid():N}";
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);

        var slowRow = MakeMessage(tenantId, MenuType, baseTime);
        var fastRow = MakeMessage(tenantId, BookingType, baseTime.AddSeconds(1));

        await SeedAsync(slowRow, fastRow);

        var transport = new LaneGateTransport(MenuType, BookingType, CrossLaneBudget);
        var service = BuildService(transport, tenantId);

        await service.PublishPendingBatchAsync(CancellationToken.None);

        Assert.True(
            transport.SlowSawFastFirst,
            $"The {MenuType} lane waited {CrossLaneBudget.TotalSeconds}s and the {BookingType} lane "
            + "never published. That is head-of-line blocking across two logical queues — the "
            + "publish phase is dispatching one sequential batch again instead of one lane per "
            + "queue (#96).");

        Assert.Contains(fastRow.MessageId, transport.Published);
        Assert.Contains(slowRow.MessageId, transport.Published);
    }

    // ── Test 2: ordering WITHIN a lane is preserved ─────────────────────────────────

    /// <summary>
    /// The guarantee the fix must not trade away. <c>booking.commands</c> declares
    /// <c>OrderingRequired: true</c>, so its rows must still leave in <c>occurred_at</c> order.
    /// This fails if anyone ever "simplifies" the fix into a flat parallel dispatch over the batch.
    /// </summary>
    [Fact]
    public async Task OrderedLane_PublishesInOccurredAtOrder()
    {
        var tenantId = $"lane-order-{Guid.NewGuid():N}";
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-5);

        var first = MakeMessage(tenantId, BookingType, baseTime);
        var second = MakeMessage(tenantId, BookingType, baseTime.AddSeconds(1));
        var third = MakeMessage(tenantId, BookingType, baseTime.AddSeconds(2));

        // Seeded out of order so a pass cannot come from insertion order.
        await SeedAsync(third, first, second);

        var transport = new LaneGateTransport(slowType: null, fastType: null, CrossLaneBudget);
        var service = BuildService(transport, tenantId);

        await service.PublishPendingBatchAsync(CancellationToken.None);

        var seen = transport.Published
            .Where(id => id == first.MessageId || id == second.MessageId || id == third.MessageId)
            .ToList();

        Assert.Equal([first.MessageId, second.MessageId, third.MessageId], seen);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static OutboxMessage MakeMessage(string tenantId, string messageType, DateTimeOffset occurredAt) =>
        new()
        {
            TenantId = tenantId,
            MessageId = $"msg-{Guid.NewGuid():N}",
            MessageType = messageType,
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
        services.AddScoped(_ => fixture.CreateContext("lane-worker"));
        services.AddTransient<IOutboxRepository, OutboxRepository>();
        services.AddTransient<IDeadLetterRepository, DeadLetterRepository>();
        services.AddTransient<IUnitOfWork, UnitOfWork>();
        services.AddSingleton(transport);

        // AFTER the repository registration — last registration wins.
        services.ScopeOutboxClaimToTenant(tenantId);

        var sp = services.BuildServiceProvider();
        return new OutboxPublisherHostedService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MessagingOptions()),
            NullLogger<OutboxPublisherHostedService>.Instance);
    }

    /// <summary>
    /// Transport that makes one lane wait for another. When <paramref name="slowType"/> and
    /// <paramref name="fastType"/> are null it is a plain recorder (Test 2).
    /// </summary>
    private sealed class LaneGateTransport(string? slowType, string? fastType, TimeSpan budget)
        : IMessageTransport
    {
        private readonly TaskCompletionSource _fastPublished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly List<string> _published = [];
        private readonly Lock _gate = new();

        /// <summary>Message ids in the order the transport actually saw them.</summary>
        public IReadOnlyList<string> Published
        {
            get { lock (_gate) { return [.. _published]; } }
        }

        /// <summary>False means the slow lane waited out its budget — i.e. it was blocking.</summary>
        public bool SlowSawFastFirst { get; private set; } = true;

        public async Task PublishAsync(JobEnvelope envelope, LogicalQueue queue, CancellationToken ct = default)
        {
            if (slowType is not null && envelope.MessageType == slowType)
            {
                var completed = await Task.WhenAny(_fastPublished.Task, Task.Delay(budget, ct));
                if (completed != _fastPublished.Task)
                    SlowSawFastFirst = false;
            }

            lock (_gate) { _published.Add(envelope.MessageId); }

            if (fastType is not null && envelope.MessageType == fastType)
                _fastPublished.TrySetResult();
        }
    }
}
