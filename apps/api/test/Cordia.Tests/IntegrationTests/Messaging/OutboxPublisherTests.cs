#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Cordia.Infrastructure.Messaging;
using Cordia.Infrastructure.Services;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Integration tests for <see cref="OutboxPublisherHostedService"/> against a real Postgres DB.
/// Covers §5.1 (claim→publish→mark), §5.2 (crash-before-mark → republish) and the
/// lease/reclaim mechanics described in §8.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class OutboxPublisherTests(PostgresFixture fixture)
{
    // ── Helpers ─────────────────────────────────────────────────────────────────────

    // Tests 1-4 use an unroutable type so the cross-tenant publisher (Test 5) does not
    // accidentally mark them published, breaking their reclaim/lease assertions.
    private static OutboxMessage MakeMessage(string tenantId, string messageType = "test.event.v1") =>
        new()
        {
            TenantId = tenantId,
            MessageId = $"msg-{Guid.NewGuid():N}",
            MessageType = messageType,
            CorrelationId = $"corr-{Guid.NewGuid():N}",
            Payload = "{}",
        };

    private OutboxPublisherHostedService BuildService(
        IMessageTransport transport,
        MessagingOptions? opts = null)
    {
        // Build a minimal ServiceProvider that resolves IOutboxRepository and IMessageTransport
        // for the publisher's per-message scopes. The DbContext is created via the fixture's
        // factory to avoid re-registering all Npgsql enum mappings.
        var services = new ServiceCollection();

        // Scoped DbContext so that, within one publisher scope, the repositories and the unit of
        // work (used by the poison dead-letter path) share ONE context — i.e. one transaction.
        services.AddScoped(_ => fixture.CreateContext("publisher-worker"));
        services.AddTransient<IOutboxRepository, OutboxRepository>();
        services.AddTransient<IDeadLetterRepository, DeadLetterRepository>();
        services.AddTransient<IUnitOfWork, UnitOfWork>();
        // Transport is a singleton so the same fake instance is shared across all scopes.
        services.AddSingleton<IMessageTransport>(transport);

        var sp = services.BuildServiceProvider();
        var options = Options.Create(opts ?? new MessagingOptions());
        return new OutboxPublisherHostedService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            options,
            NullLogger<OutboxPublisherHostedService>.Instance);
    }

    // Neutralizes any pre-existing unpublished rows so the cross-tenant ClaimBatchAsync
    // (ORDER BY occurred_at LIMIT N) deterministically reaches THIS test's freshly-seeded
    // rows. The shared Cordia_API_Test DB accumulates never-published detritus across runs;
    // once that exceeds the batch limit it starves new rows out of the claim window (flaky).
    // Marking published (rather than deleting) keeps row COUNTS stable, so parallel
    // acceptance-collection tests that assert outbox count/existence are unaffected.
    private async Task NeutralizeExistingUnpublishedAsync()
    {
        await using var ctx = fixture.CreateContext("outbox-pub-cleanup");
        await ctx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.PublishedAt == null)
            .ExecuteUpdateAsync(b => b
                .SetProperty(m => m.PublishedAt, DateTimeOffset.UtcNow)
                .SetProperty(m => m.LeaseOwner, (string?)null)
                .SetProperty(m => m.LeaseExpiresAt, (DateTimeOffset?)null));
    }

    // ── Test 1: ClaimBatchAsync sets lease and is cross-tenant ──────────────────────

    [Fact]
    public async Task ClaimBatchAsync_SetsLease_AndIsCrossTenant()
    {
        await NeutralizeExistingUnpublishedAsync();
        var tenantA = $"t3-pub-a-{Guid.NewGuid():N}";
        var tenantB = $"t3-pub-b-{Guid.NewGuid():N}";
        var leaseOwner = $"test-owner-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var leaseExp = now.AddSeconds(30);

        // Seed one row per tenant.
        await using (var ctx = fixture.CreateContext(tenantA))
        {
            ctx.OutboxMessages.Add(MakeMessage(tenantA));
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = fixture.CreateContext(tenantB))
        {
            ctx.OutboxMessages.Add(MakeMessage(tenantB));
            await ctx.SaveChangesAsync();
        }

        // Claim cross-tenant via a context scoped to neither tenant.
        await using var workerCtx = fixture.CreateContext("worker-tenant");
        var repo = new OutboxRepository(workerCtx);
        var claimed = await repo.ClaimBatchAsync(10, leaseOwner, leaseExp, now);

        // Should see both rows (cross-tenant).
        var claimedTenants = claimed.Select(r => r.TenantId).ToHashSet();
        Assert.Contains(tenantA, claimedTenants);
        Assert.Contains(tenantB, claimedTenants);

        // Re-read in fresh contexts to confirm lease was persisted.
        await using var freshA = fixture.CreateContext(tenantA);
        var rowA = await freshA.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantA)
            .FirstOrDefaultAsync();
        Assert.NotNull(rowA);
        Assert.Equal(leaseOwner, rowA!.LeaseOwner);
        Assert.NotNull(rowA.LeaseExpiresAt);
        Assert.Equal(1, rowA.PublishAttempts);
    }

    // ── Test 2: Lease excludes already-leased rows; reclaim after expiry ────────────
    // This test uses direct DB inspection (not cross-tenant batch results) to verify
    // lease semantics for its specific row, avoiding interference from parallel tests.

    [Fact]
    public async Task ClaimBatchAsync_ExcludesLeasedRows_ReclainsAfterExpiry()
    {
        await NeutralizeExistingUnpublishedAsync();
        var tenantId = $"t3-lease-{Guid.NewGuid():N}";
        var leaseOwner = $"test-owner-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var msg = MakeMessage(tenantId); // unroutable type — safe from cross-test publisher

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.OutboxMessages.Add(msg);
            await ctx.SaveChangesAsync();
        }

        // Claim using our own short-lived lease window.
        var leaseExp = now.AddSeconds(30);
        await using (var ctx1 = fixture.CreateContext("worker"))
        {
            var repo1 = new OutboxRepository(ctx1);
            await repo1.ClaimBatchAsync(50, leaseOwner, leaseExp, now);
        }

        // Verify the row now has our lease (direct DB read, tenant-scoped).
        await using (var checkCtx = fixture.CreateContext(tenantId))
        {
            var row = await checkCtx.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.MessageId == msg.MessageId)
                .FirstAsync();
            Assert.Equal(leaseOwner, row.LeaseOwner);
        }

        // Second claim before expiry (nowBeforeExp < leaseExp): the ORIGINAL leaseOwner's lock
        // is in the future so the row must NOT switch to "other-owner".
        var nowBeforeExp = now.AddSeconds(10); // still before leaseExp
        await using (var ctx2 = fixture.CreateContext("worker"))
        {
            var repo2 = new OutboxRepository(ctx2);
            await repo2.ClaimBatchAsync(50, "other-owner", leaseExp.AddSeconds(30), nowBeforeExp);
        }

        // The lease owner must still be the original one (row was excluded from second claim).
        await using (var checkCtx2 = fixture.CreateContext(tenantId))
        {
            var row = await checkCtx2.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.MessageId == msg.MessageId)
                .FirstAsync();
            Assert.Equal(leaseOwner, row.LeaseOwner);
        }

        // Third claim after expiry: nowAfterExp > leaseExp so the row is reclaimable.
        // We use a time well past the original lease and any concurrent publisher's lease
        // by passing nowAfterExp = now + 120s so that even publisher leases (30s) are expired.
        var nowAfterExp = now.AddSeconds(120);
        await using (var ctx3 = fixture.CreateContext("worker"))
        {
            var repo3 = new OutboxRepository(ctx3);
            await repo3.ClaimBatchAsync(50, "reclaim-owner", nowAfterExp.AddSeconds(30), nowAfterExp);
        }

        // The row must now have the reclaim-owner's lease.
        await using (var checkCtx3 = fixture.CreateContext(tenantId))
        {
            var row = await checkCtx3.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.MessageId == msg.MessageId)
                .FirstAsync();
            Assert.Equal("reclaim-owner", row.LeaseOwner);
        }
    }

    // ── Test 3: SKIP LOCKED — two concurrent transactions claim different rows ───────

    [Fact]
    public async Task ClaimBatchAsync_SkipLocked_TwoConcurrentClaimsDifferentRows()
    {
        await NeutralizeExistingUnpublishedAsync();
        var tenantId = $"t3-skip-{Guid.NewGuid():N}";
        var msgIdA = $"msg-{Guid.NewGuid():N}";
        var msgIdB = $"msg-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = msgIdA,
                MessageType = "test.event.v1",
                CorrelationId = "corr-a",
                Payload = "{}",
            });
            ctx.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = msgIdB,
                MessageType = "test.event.v1",
                CorrelationId = "corr-b",
                Payload = "{}",
            });
            await ctx.SaveChangesAsync();
        }

        // Two concurrent raw transactions each claim 1 row — they should get different rows.
        await using var ctxA = fixture.CreateContext("worker");
        await using var txA = await ctxA.Database.BeginTransactionAsync();
        var rowsA = await ctxA.Set<OutboxMessage>()
            .FromSqlRaw(
                "SELECT * FROM public.outbox_messages " +
                "WHERE published_at IS NULL AND (lease_owner IS NULL OR lease_expires_at < {0}) " +
                "ORDER BY occurred_at LIMIT 1 FOR UPDATE SKIP LOCKED",
                now)
            .IgnoreQueryFilters()
            .ToListAsync();
        Assert.Single(rowsA);

        await using var ctxB = fixture.CreateContext("worker");
        await using var txB = await ctxB.Database.BeginTransactionAsync();
        var rowsB = await ctxB.Set<OutboxMessage>()
            .FromSqlRaw(
                "SELECT * FROM public.outbox_messages " +
                "WHERE published_at IS NULL AND (lease_owner IS NULL OR lease_expires_at < {0}) " +
                "ORDER BY occurred_at LIMIT 1 FOR UPDATE SKIP LOCKED",
                now)
            .IgnoreQueryFilters()
            .ToListAsync();
        Assert.Single(rowsB);

        Assert.NotEqual(rowsA[0].MessageId, rowsB[0].MessageId);

        await txA.CommitAsync();
        await txB.CommitAsync();
    }

    // ── Test 4: MarkPublishedAsync sets published_at, clears lease ──────────────────

    [Fact]
    public async Task MarkPublishedAsync_SetsPublishedAt_ClearsLease_RowNotReclaimed()
    {
        await NeutralizeExistingUnpublishedAsync();
        var tenantId = $"t3-mark-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var leaseExp = now.AddSeconds(30);
        var msg = MakeMessage(tenantId);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.OutboxMessages.Add(msg);
            await ctx.SaveChangesAsync();
        }

        // Claim the row.
        await using (var ctx = fixture.CreateContext("worker"))
        {
            var repo = new OutboxRepository(ctx);
            var batch = await repo.ClaimBatchAsync(1, "owner-1", leaseExp, now);
            Assert.Single(batch);
        }

        // Mark published.
        var publishedAt = DateTimeOffset.UtcNow;
        await using (var ctx = fixture.CreateContext("worker"))
        {
            var repo = new OutboxRepository(ctx);
            await repo.MarkPublishedAsync(tenantId, msg.MessageId, publishedAt);
        }

        // Re-read: published_at set, lease cleared.
        await using var freshCtx = fixture.CreateContext(tenantId);
        var row = await freshCtx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId && m.MessageId == msg.MessageId)
            .FirstAsync();

        Assert.NotNull(row.PublishedAt);
        Assert.Null(row.LeaseOwner);
        Assert.Null(row.LeaseExpiresAt);

        // A subsequent claim should NOT return it (published_at is now set).
        await using (var ctx = fixture.CreateContext("worker"))
        {
            var repo = new OutboxRepository(ctx);
            var reClaim = await repo.ClaimBatchAsync(10, "owner-2", leaseExp.AddMinutes(1), now.AddSeconds(60));
            Assert.DoesNotContain(reClaim, r => r.MessageId == msg.MessageId);
        }
    }

    // ── Test 5: PublishPendingBatchAsync happy path ──────────────────────────────────

    [Fact]
    public async Task PublishPendingBatchAsync_PublishesBothRows_MarksPublished()
    {
        await NeutralizeExistingUnpublishedAsync();
        var tenantId = $"t3-batch-{Guid.NewGuid():N}";
        var msgA = MakeMessage(tenantId, "booking.command.requested.v1");
        var msgB = MakeMessage(tenantId, "booking.command.requested.v1");

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.OutboxMessages.Add(msgA);
            ctx.OutboxMessages.Add(msgB);
            await ctx.SaveChangesAsync();
        }

        // Fake transport that records published calls.
        var published = new List<(JobEnvelope Envelope, LogicalQueue Queue)>();
        var fakeTransport = new Mock<IMessageTransport>();
        fakeTransport
            .Setup(t => t.PublishAsync(It.IsAny<JobEnvelope>(), It.IsAny<LogicalQueue>(), It.IsAny<CancellationToken>()))
            .Callback<JobEnvelope, LogicalQueue, CancellationToken>((e, q, _) => published.Add((e, q)))
            .Returns(Task.CompletedTask);

        var service = BuildService(fakeTransport.Object);
        var count = await service.PublishPendingBatchAsync(CancellationToken.None);

        // The publisher is cross-tenant; count may be >= 2 if other tests left unpublished rows.
        Assert.True(count >= 2, $"Expected at least 2 published, got {count}");

        // Our specific messages must be in the published set, routed to BookingCommands.
        Assert.Contains(published, p => p.Envelope.MessageId == msgA.MessageId && p.Queue == LogicalQueue.BookingCommands);
        Assert.Contains(published, p => p.Envelope.MessageId == msgB.MessageId && p.Queue == LogicalQueue.BookingCommands);

        // Verify OUR rows are marked in the DB.
        await using var freshCtx = fixture.CreateContext(tenantId);
        var rows = await freshCtx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId)
            .ToListAsync();

        Assert.All(rows, r => Assert.NotNull(r.PublishedAt));
    }

    // ── Test 6: Crash before mark → republish (§5.2 substrate invariant) ────────────

    [Fact]
    public async Task PublishPendingBatchAsync_CrashBeforeMark_RowRepublishedAfterLeaseExpiry()
    {
        await NeutralizeExistingUnpublishedAsync();
        var tenantId = $"t3-crash-{Guid.NewGuid():N}";
        var msg = MakeMessage(tenantId, "booking.command.requested.v1");

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.OutboxMessages.Add(msg);
            await ctx.SaveChangesAsync();
        }

        // Per-message-id call tracking: the publisher tick is cross-tenant, so a parallel
        // acceptance-collection test could insert a routable row that this tick also claims.
        // Scoping the throw + the count assertion to OUR crash row keeps the test deterministic
        // regardless of such concurrent rows (which the mock simply completes and ignores).
        var callsForCrashRow = 0;
        var fakeTransport = new Mock<IMessageTransport>();
        fakeTransport
            .Setup(t => t.PublishAsync(It.IsAny<JobEnvelope>(), It.IsAny<LogicalQueue>(), It.IsAny<CancellationToken>()))
            .Returns<JobEnvelope, LogicalQueue, CancellationToken>((e, _, _) =>
            {
                if (e.MessageId != msg.MessageId)
                    return Task.CompletedTask;          // unrelated concurrent row — ignore

                callsForCrashRow++;
                if (callsForCrashRow == 1)
                    throw new InvalidOperationException("simulated crash before mark");
                return Task.CompletedTask;
            });

        // Use a very short lease (1 second) so the second tick can reclaim.
        var opts = new MessagingOptions
        {
            Publisher = new OutboxPublisherOptions
            {
                LeaseSeconds = 1,
                BatchSize = 50,
                PollIntervalMs = 250,
                Enabled = true,
            },
        };

        var service = BuildService(fakeTransport.Object, opts);

        // First tick: transport throws for OUR row → it is NOT marked published.
        await service.PublishPendingBatchAsync(CancellationToken.None);
        await using (var freshCtx = fixture.CreateContext(tenantId))
        {
            var row = await freshCtx.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.MessageId == msg.MessageId)
                .FirstAsync();
            Assert.Null(row.PublishedAt);
        }

        // Wait for the 1s lease to expire.
        await Task.Delay(TimeSpan.FromSeconds(2));

        // Second tick: reclaims the expired lease → transport called AGAIN for the same message_id.
        await service.PublishPendingBatchAsync(CancellationToken.None);

        // Our crash row was delivered exactly twice (duplicate delivery; inbox dedups in T4),
        // then marked published.
        Assert.Equal(2, callsForCrashRow);
        await using (var freshCtx = fixture.CreateContext(tenantId))
        {
            var row = await freshCtx.OutboxMessages
                .IgnoreQueryFilters()
                .Where(m => m.MessageId == msg.MessageId)
                .FirstAsync();
            Assert.NotNull(row.PublishedAt);
        }
    }

    // ── Test 7: unroutable message_type → dead-lettered (no infinite reclaim loop) ───

    [Fact]
    public async Task PublishPendingBatchAsync_UnroutableMessageType_DeadLetters_AndMarksPublished()
    {
        await NeutralizeExistingUnpublishedAsync();
        var tenantId = $"t3-poison-{Guid.NewGuid():N}";
        var msg = MakeMessage(tenantId, "unknown.domain.requested.v1"); // no logical-queue prefix → unroutable

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.OutboxMessages.Add(msg);
            await ctx.SaveChangesAsync();
        }

        var fakeTransport = new Mock<IMessageTransport>();
        fakeTransport
            .Setup(t => t.PublishAsync(It.IsAny<JobEnvelope>(), It.IsAny<LogicalQueue>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = BuildService(fakeTransport.Object);
        await service.PublishPendingBatchAsync(CancellationToken.None);

        await using var freshCtx = fixture.CreateContext(tenantId);

        // The row is marked published → removed from the claim set (no infinite reclaim).
        var row = await freshCtx.OutboxMessages
            .IgnoreQueryFilters()
            .Where(m => m.MessageId == msg.MessageId)
            .FirstAsync();
        Assert.NotNull(row.PublishedAt);

        // A dead-letter row was written (synthetic consumer, quarantined, correct error class).
        var dlq = await freshCtx.DeadLetterMessages
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId && d.MessageId == msg.MessageId)
            .FirstOrDefaultAsync();
        Assert.NotNull(dlq);
        Assert.Equal("outbox.publisher", dlq!.ConsumerName);
        Assert.Equal(DlqReplayState.Quarantined, dlq.ReplayState);
        Assert.Equal("unroutable_message_type", dlq.ErrorClass);

        // The transport is never invoked for an unroutable message.
        fakeTransport.Verify(
            t => t.PublishAsync(
                It.Is<JobEnvelope>(e => e.MessageId == msg.MessageId),
                It.IsAny<LogicalQueue>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
