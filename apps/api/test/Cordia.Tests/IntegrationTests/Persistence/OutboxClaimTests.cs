#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class OutboxClaimTests(PostgresFixture fixture)
{
    // (1) Partial index ix_outbox_unpublished exists
    [Fact]
    public async Task PartialIndex_IxOutboxUnpublished_Exists()
    {
        await using var ctx = fixture.CreateContext("t3-ob-idx");
        var exists = await ctx.Database
            .SqlQueryRaw<int>(
                "SELECT 1 AS value FROM pg_indexes WHERE indexname = 'ix_outbox_unpublished'")
            .AnyAsync();
        Assert.True(exists);
    }

    // (2) SKIP LOCKED proof: two concurrent contexts claim different rows
    [Fact]
    public async Task ClaimUnpublished_SkipLocked_TwoConcurrentContextsClaimDifferentRows()
    {
        var tenantId = $"t3-ob-{Guid.NewGuid():N}";
        var msgIdA = $"msg-{Guid.NewGuid():N}";
        var msgIdB = $"msg-{Guid.NewGuid():N}";

        // Insert 2 unpublished messages
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

        // Open ctxA, begin transaction, claim 1 row (holds lock)
        await using var ctxA = fixture.CreateContext(tenantId);
        await using var txA = await ctxA.Database.BeginTransactionAsync();
        var claimedA = await ctxA.Set<OutboxMessage>()
            .FromSqlRaw(
                "SELECT * FROM public.outbox_messages WHERE tenant_id = {0} AND published_at IS NULL ORDER BY occurred_at LIMIT {1} FOR UPDATE SKIP LOCKED",
                tenantId, 1)
            .ToListAsync();
        Assert.Single(claimedA);

        // Open ctxB, begin transaction, claim 1 row — should get the OTHER row (skips locked)
        await using var ctxB = fixture.CreateContext(tenantId);
        await using var txB = await ctxB.Database.BeginTransactionAsync();
        var claimedB = await ctxB.Set<OutboxMessage>()
            .FromSqlRaw(
                "SELECT * FROM public.outbox_messages WHERE tenant_id = {0} AND published_at IS NULL ORDER BY occurred_at LIMIT {1} FOR UPDATE SKIP LOCKED",
                tenantId, 1)
            .ToListAsync();
        Assert.Single(claimedB);

        // The two claimed rows must differ
        Assert.NotEqual(claimedA[0].MessageId, claimedB[0].MessageId);

        await txA.CommitAsync();
        await txB.CommitAsync();
    }

    // (3) Worker-authoritative claim: ClaimUnpublishedAsync scopes to the tenantId ARGUMENT,
    //     not the ambient context tenant (a publisher worker is not request-scoped). A context
    //     scoped to tenant-B claiming for tenant-A must return tenant-A's message.
    [Fact]
    public async Task ClaimUnpublishedAsync_ScopesToTenantArgument_NotAmbientContext()
    {
        var tenantA = $"t3-obA-{Guid.NewGuid():N}";
        var tenantB = $"t3-obB-{Guid.NewGuid():N}";
        var msgId = $"msg-{Guid.NewGuid():N}";

        await using (var ctxA = fixture.CreateContext(tenantA))
        {
            ctxA.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantA,
                MessageId = msgId,
                MessageType = "test.event.v1",
                CorrelationId = "corr-a",
                Payload = "{}",
            });
            await ctxA.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Context scoped to tenant-B, but claim for tenant-A.
        await using var workerCtx = fixture.CreateContext(tenantB);
        var repo = new OutboxRepository(workerCtx);
        var claimed = await repo.ClaimUnpublishedAsync(tenantA, 10, TestContext.Current.CancellationToken);

        Assert.Single(claimed);
        Assert.Equal(tenantA, claimed[0].TenantId);
        Assert.Equal(msgId, claimed[0].MessageId);
    }
}
