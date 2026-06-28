#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class DeadLetterMessagePersistenceTests(PostgresFixture fixture)
{
    // (1) Insert and read back → ReplayState + jsonb round-trip
    [Fact]
    public async Task Insert_ThenReadBack_RoundTripsReplayStateAndJsonb()
    {
        var tenantId = $"t3-dlq-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var consumer = "payments.dlq";
        var attemptHistory = """[{"attempt":1,"error":"timeout"}]""";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.DeadLetterMessages.Add(new DeadLetterMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                ConsumerName = consumer,
                MessageType = "booking.finalize.requested.v1",
                Payload = """{"ref":"abc"}""",
                ErrorClass = "TimeoutException",
                AttemptHistory = attemptHistory,
                FirstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                ReplayState = DlqReplayState.Quarantined,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var row = await readCtx.DeadLetterMessages
            .AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.MessageId == messageId && e.ConsumerName == consumer);

        Assert.Equal(DlqReplayState.Quarantined, row.ReplayState);
        // jsonb normalises key order — verify the attempt array contains the expected entry
        using var doc = JsonDocument.Parse(row.AttemptHistory);
        var arr = doc.RootElement;
        Assert.Equal(JsonValueKind.Array, arr.ValueKind);
        Assert.Equal(1, arr.GetArrayLength());
        var entry = arr[0];
        Assert.Equal(1, entry.GetProperty("attempt").GetInt32());
        Assert.Equal("timeout", entry.GetProperty("error").GetString());
    }

    // (2a) AddAsync duplicate (FIX C4) — DeadLetterRepository.AddAsync must be idempotent:
    //      calling it twice with the same (tenant, message_id, consumer) must NOT throw and must
    //      leave exactly ONE row in dead_letter_messages.
    [Fact]
    public async Task AddAsync_DuplicateKey_IsIdempotentAndLeavesExactlyOneRow()
    {
        var tenantId = $"t3-dlq-idem-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var consumer = "booking.driver";

        var message = new DeadLetterMessage
        {
            TenantId = tenantId,
            MessageId = messageId,
            ConsumerName = consumer,
            MessageType = "booking.finalize.requested.v1",
            Payload = "{}",
            ErrorClass = "poison",
            AttemptHistory = "[]",
            FirstFailedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            ReplayState = DlqReplayState.Quarantined,
        };

        // First call — should succeed.
        await using (var ctx1 = fixture.CreateContext(tenantId))
        {
            var repo1 = new DeadLetterRepository(ctx1);
            await repo1.AddAsync(message, CancellationToken.None);
        }

        // Second call with the same key — must NOT throw (ON CONFLICT DO NOTHING).
        var secondException = await Record.ExceptionAsync(async () =>
        {
            await using var ctx2 = fixture.CreateContext(tenantId);
            var repo2 = new DeadLetterRepository(ctx2);
            await repo2.AddAsync(message, CancellationToken.None);
        });
        Assert.Null(secondException);

        // Assert: exactly ONE dead_letter_messages row for this key.
        await using var assertCtx = fixture.CreateContext(tenantId);
        var count = await assertCtx.DeadLetterMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(d =>
                d.TenantId == tenantId &&
                d.MessageId == messageId &&
                d.ConsumerName == consumer);

        Assert.Equal(1, count);
    }

    // (2) Duplicate (TenantId, MessageId, ConsumerName) → unique_violation
    [Fact]
    public async Task Insert_DuplicateKey_ThrowsUniqueViolation()
    {
        var tenantId = $"t3-dlq-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var consumer = "payments.dlq";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.DeadLetterMessages.Add(new DeadLetterMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                ConsumerName = consumer,
                MessageType = "test.v1",
                Payload = "{}",
                ErrorClass = "Error",
                AttemptHistory = "[]",
                FirstFailedAt = DateTimeOffset.UtcNow,
                ReplayState = DlqReplayState.Quarantined,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = fixture.CreateContext(tenantId);
        ctx2.DeadLetterMessages.Add(new DeadLetterMessage
        {
            TenantId = tenantId,
            MessageId = messageId, // same composite PK
            ConsumerName = consumer,
            MessageType = "test.v1",
            Payload = "{}",
            ErrorClass = "Error",
            AttemptHistory = "[]",
            FirstFailedAt = DateTimeOffset.UtcNow,
            ReplayState = DlqReplayState.Quarantined,
        });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx2.SaveChangesAsync());
        var pgEx = ex.InnerException as PostgresException;
        Assert.NotNull(pgEx);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pgEx.SqlState);
    }
}
