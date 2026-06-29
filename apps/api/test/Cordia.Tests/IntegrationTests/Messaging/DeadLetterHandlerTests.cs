#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Messaging;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Integration tests for <see cref="DeadLetterHandler"/> (spec 05 §5.3, §8, T7).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class DeadLetterHandlerTests(PostgresFixture fixture)
{
    // ── Test 1: Quarantine happy path ─────────────────────────────────────────────

    [Fact]
    public async Task QuarantineAsync_InsertsRowWithExpectedFields()
    {
        var tenantId = $"t7-dlq-a-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer";
        var payload = JsonSerializer.Serialize(new { operation_id = operationId });
        var attemptHistory = """[{"attempt":1,"error":"timeout"}]""";
        var firstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        var envelope = TestEnvelopes.Build(tenantId, messageId, "booking.command.requested.v1", payload);

        await using var ctx = fixture.CreateContext(tenantId);
        var dlqRepo = new DeadLetterRepository(ctx);
        var handler = new DeadLetterHandler(dlqRepo, NullLogger<DeadLetterHandler>.Instance);

        await handler.QuarantineAsync(
            envelope, consumer, "TimeoutException", attemptHistory, firstFailedAt);

        // Read back and verify
        await using var readCtx = fixture.CreateContext(tenantId);
        var row = await readCtx.DeadLetterMessages
            .AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.MessageId == messageId && e.ConsumerName == consumer);

        Assert.Equal(DlqReplayState.Quarantined, row.ReplayState);
        Assert.Equal("TimeoutException", row.ErrorClass);
        Assert.Equal("booking.command.requested.v1", row.MessageType);

        // Payload round-trip — jsonb may reorder keys but operation_id must be present
        using var doc = JsonDocument.Parse(row.Payload);
        Assert.True(doc.RootElement.TryGetProperty("operation_id", out var opProp));
        Assert.Equal(operationId, opProp.GetString());

        // AttemptHistory jsonb round-trip
        using var histDoc = JsonDocument.Parse(row.AttemptHistory);
        Assert.Equal(JsonValueKind.Array, histDoc.RootElement.ValueKind);
        Assert.Equal(1, histDoc.RootElement.GetArrayLength());
    }

    // ── Test 2: Default attempt history when null/empty ──────────────────────────

    [Fact]
    public async Task QuarantineAsync_NullAttemptHistory_DefaultsToEmptyArray()
    {
        var tenantId = $"t7-dlq-b-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer";
        var envelope = TestEnvelopes.Build(tenantId, messageId, "booking.command.requested.v1", "{}");

        await using var ctx = fixture.CreateContext(tenantId);
        var dlqRepo = new DeadLetterRepository(ctx);
        var handler = new DeadLetterHandler(dlqRepo, NullLogger<DeadLetterHandler>.Instance);

        await handler.QuarantineAsync(
            envelope, consumer, "Error", attemptHistoryJson: null!, firstFailedAt: DateTimeOffset.UtcNow);

        await using var readCtx = fixture.CreateContext(tenantId);
        var row = await readCtx.DeadLetterMessages
            .AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.MessageId == messageId);

        using var doc = JsonDocument.Parse(row.AttemptHistory);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    // ── Test 3: Inv-14 pre-DLQ ordering + idempotency ────────────────────────────
    //
    // Sequence:
    //   txn1 — persist BUSINESS STATE (OperationStatus → ManualReview) + ops work-item, COMMIT.
    //   Then — call QuarantineAsync TWICE (same PK) to simulate crash-before-ACK redelivery.
    //   Since FIX C4 makes AddAsync idempotent (ON CONFLICT DO NOTHING), the second call must
    //   NOT throw, must leave exactly ONE dead_letter_messages row, and business state from txn1
    //   must remain intact.
    //
    // This proves Inv-14: the customer never becomes invisible because a message changed queues.
    // Business state is durably persisted BEFORE dead-lettering; a duplicate DLQ write (from
    // redelivery) is now silently suppressed rather than aborting the consumer with a PK violation.

    [Fact]
    public async Task Inv14_BusinessStatePersistedBeforeDeadLettering()
    {
        var tenantId = $"t7-dlq-c-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer";
        var envelope = TestEnvelopes.Build(tenantId, messageId, "booking.command.requested.v1", "{}");

        // ── txn1: persist business state (operation → ManualReview) + work-item ──
        string operationId;
        var workItemId = $"wi-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            // Seed a booking operation
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            // Update the operation status to ManualReview (recoverable/terminal status)
            await ctx.BookingOperations
                .IgnoreQueryFilters()
                .Where(o => o.TenantId == tenantId && o.OperationId == operationId)
                .ExecuteUpdateAsync(b => b.SetProperty(x => x.Status, OperationStatus.ManualReview));

            // Add an ops work-item (the "ops work-item" Inv-14 mandates before DLQ)
            ctx.OperationWorkItems.Add(new OperationWorkItem
            {
                TenantId = tenantId,
                WorkItemId = workItemId,
                OperationId = operationId,
                Reason = "requires_manual_review",
                State = "open",
                SlaDeadline = DateTimeOffset.UtcNow.AddHours(24),
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }
        // txn1 committed.

        // ── First QuarantineAsync call: quarantine the message ────────────────────
        await using (var ctx1 = fixture.CreateContext(tenantId))
        {
            var dlqRepo1 = new DeadLetterRepository(ctx1);
            var handler1 = new DeadLetterHandler(dlqRepo1, NullLogger<DeadLetterHandler>.Instance);
            await handler1.QuarantineAsync(envelope, consumer, "Error", "[]", DateTimeOffset.UtcNow);
        }

        // ── Second QuarantineAsync call (same PK — crash-before-ACK redelivery) ───
        // FIX C4: must NOT throw; ON CONFLICT DO NOTHING silently suppresses the duplicate.
        await using (var ctx2 = fixture.CreateContext(tenantId))
        {
            var dlqRepo2 = new DeadLetterRepository(ctx2);
            var handler2 = new DeadLetterHandler(dlqRepo2, NullLogger<DeadLetterHandler>.Instance);
            var secondCallEx = await Record.ExceptionAsync(
                () => handler2.QuarantineAsync(envelope, consumer, "Error", "[]", DateTimeOffset.UtcNow));
            Assert.Null(secondCallEx); // idempotent — no exception on duplicate
        }

        // ── Assert: exactly ONE dead_letter_messages row ───────────────────────────
        await using var verifyCtx = fixture.CreateContext(tenantId);

        var dlqCount = await verifyCtx.DeadLetterMessages
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(d => d.TenantId == tenantId && d.MessageId == messageId && d.ConsumerName == consumer);
        Assert.Equal(1, dlqCount);

        // ── Assert: business state from txn1 is STILL durable ────────────────────
        var operation = await verifyCtx.BookingOperations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        // Business state persisted: ManualReview is still there
        Assert.Equal(OperationStatus.ManualReview, operation.Status);

        // Ops work-item persisted
        var workItem = await verifyCtx.OperationWorkItems
            .AsNoTracking()
            .SingleAsync(w => w.WorkItemId == workItemId);
        Assert.Equal("open", workItem.State);
    }
}
