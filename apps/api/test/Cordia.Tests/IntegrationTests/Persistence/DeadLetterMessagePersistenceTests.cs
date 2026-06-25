#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
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
