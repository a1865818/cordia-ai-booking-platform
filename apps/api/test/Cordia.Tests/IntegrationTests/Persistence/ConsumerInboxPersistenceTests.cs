#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ConsumerInboxPersistenceTests(PostgresFixture fixture)
{
    // (1) AddAsync admitted row; GetAsync returns it (Outcome round-trip)
    [Fact]
    public async Task AddAsync_ThenGetAsync_RoundTripsOutcome()
    {
        var tenantId = $"t3-ci-{Guid.NewGuid():N}";
        var consumer = "payments.v1";
        var messageId = $"msg-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.ConsumerInbox.Add(new ConsumerInbox
            {
                TenantId = tenantId,
                ConsumerName = consumer,
                MessageId = messageId,
                Outcome = InboxOutcome.Admitted,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var row = await readCtx.ConsumerInbox
            .AsNoTracking()
            .SingleAsync(e => e.ConsumerName == consumer && e.MessageId == messageId);

        Assert.Equal(InboxOutcome.Admitted, row.Outcome);
        Assert.Equal(tenantId, row.TenantId);
    }

    // (2) Insert duplicate (ConsumerName, TenantId, MessageId) → unique_violation (already-processed path)
    [Fact]
    public async Task Insert_DuplicateKey_ThrowsUniqueViolation()
    {
        var tenantId = $"t3-ci-{Guid.NewGuid():N}";
        var consumer = "payments.v1";
        var messageId = $"msg-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.ConsumerInbox.Add(new ConsumerInbox
            {
                TenantId = tenantId,
                ConsumerName = consumer,
                MessageId = messageId,
                Outcome = InboxOutcome.Admitted,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = fixture.CreateContext(tenantId);
        ctx2.ConsumerInbox.Add(new ConsumerInbox
        {
            TenantId = tenantId,
            ConsumerName = consumer,
            MessageId = messageId, // same composite PK → unique violation
            Outcome = InboxOutcome.Admitted,
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx2.SaveChangesAsync());
        var pgEx = ex.InnerException as PostgresException;
        Assert.NotNull(pgEx);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pgEx.SqlState);
    }
}
