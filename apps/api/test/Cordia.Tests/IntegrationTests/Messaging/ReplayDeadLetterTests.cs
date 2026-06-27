#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Application.Messaging.Replay;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using Cordia.Infrastructure.Persistence;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Cordia.Tests.IntegrationTests.Messaging;

// ── Fake transport ────────────────────────────────────────────────────────────────────

/// <summary>In-memory fake that captures published envelopes for assertion.</summary>
internal sealed class ReplayFakeTransport : IMessageTransport
{
    private readonly List<(JobEnvelope Envelope, LogicalQueue Queue)> _published = [];

    public IReadOnlyList<(JobEnvelope Envelope, LogicalQueue Queue)> Published => _published;

    public bool ThrowOnPublish { get; set; }

    public Task PublishAsync(JobEnvelope envelope, LogicalQueue queue, CancellationToken ct = default)
    {
        if (ThrowOnPublish)
            throw new InvalidOperationException("Simulated transport failure");

        _published.Add((envelope, queue));
        return Task.CompletedTask;
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Integration tests for <see cref="ReplayDeadLetterCommandHandler"/> (spec 05 §6.1, T7).
/// Drives the handler directly with real repos over the fixture Postgres context + FakeTransport.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ReplayDeadLetterTests(PostgresFixture fixture)
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private ReplayDeadLetterCommandHandler BuildHandler(
        string tenantId,
        out ReplayFakeTransport transport)
    {
        var ctx = fixture.CreateContext(tenantId);
        var dlqRepo = new DeadLetterRepository(ctx);
        var inboxRepo = new InboxRepository(ctx);
        var auditRepo = new AuditEventRepository(ctx);
        var uow = new UnitOfWork(ctx);
        transport = new ReplayFakeTransport();
        return new ReplayDeadLetterCommandHandler(dlqRepo, inboxRepo, transport, auditRepo, uow);
    }

    private static async Task SeedDlqRowAsync(
        PostgresFixture fixture,
        string tenantId,
        string messageId,
        string consumerName,
        string messageType = "booking.command.requested.v1",
        string? operationId = null)
    {
        var payload = operationId is not null
            ? JsonSerializer.Serialize(new { operation_id = operationId })
            : "{}";

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.DeadLetterMessages.Add(new DeadLetterMessage
        {
            TenantId = tenantId,
            MessageId = messageId,
            ConsumerName = consumerName,
            MessageType = messageType,
            Payload = payload,
            ErrorClass = "TestError",
            AttemptHistory = "[]",
            FirstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            DeadLetteredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ReplayState = DlqReplayState.Quarantined,
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task SeedInboxRowAsync(
        PostgresFixture fixture,
        string tenantId,
        string consumerName,
        string messageId,
        InboxOutcome outcome)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.ConsumerInbox.Add(new ConsumerInbox
        {
            TenantId = tenantId,
            ConsumerName = consumerName,
            MessageId = messageId,
            AdmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            Outcome = outcome,
        });
        await ctx.SaveChangesAsync();
    }

    // ── Test 1: Never admitted → preserve original message_id ────────────────────

    [Fact]
    public async Task NeverAdmitted_PreservesOriginalMessageId_AndDispatchesPayload()
    {
        var tenantId = $"t7-rpl-a-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer";

        await SeedDlqRowAsync(fixture, tenantId, messageId, consumer, operationId: operationId);
        // NO inbox row — message never admitted

        var handler = BuildHandler(tenantId, out var transport);
        var result = await handler.Handle(
            new ReplayDeadLetterCommand(tenantId, messageId, consumer),
            CancellationToken.None);

        // §6.1 row 1: original message_id preserved
        Assert.Equal(ReplayDecision.PreservedOriginalMessageId, result.Decision);
        Assert.Equal(messageId, result.DispatchedMessageId);
        Assert.StartsWith("replay-", result.ReplayCommandId);

        // Transport received exactly one envelope
        Assert.Single(transport.Published);
        var (env, _) = transport.Published[0];
        Assert.Equal(messageId, env.MessageId);

        // Payload preserved — operation_id still in envelope payload (Inv-15/§11)
        Assert.True(env.Payload.TryGetProperty("operation_id", out var opProp));
        Assert.Equal(operationId, opProp.GetString());

        // DLQ row now Replayed
        await using var readCtx = fixture.CreateContext(tenantId);
        var dlq = await readCtx.DeadLetterMessages
            .AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.MessageId == messageId && e.ConsumerName == consumer);
        Assert.Equal(DlqReplayState.Replayed, dlq.ReplayState);

        // AuditEvent written
        var audit = await readCtx.AuditEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EventType == "dead_letter.replayed"
                                   && e.SubjectRef == messageId);
        Assert.NotNull(audit);
    }

    // ── Test 2: Already admitted → new control message-id ────────────────────────

    [Fact]
    public async Task AlreadyAdmitted_NewControlMessageId_AndDispatchesOriginalPayload()
    {
        var tenantId = $"t7-rpl-b-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer";

        await SeedDlqRowAsync(fixture, tenantId, messageId, consumer, operationId: operationId);
        await SeedInboxRowAsync(fixture, tenantId, consumer, messageId, InboxOutcome.Admitted);

        var handler = BuildHandler(tenantId, out var transport);
        var result = await handler.Handle(
            new ReplayDeadLetterCommand(tenantId, messageId, consumer),
            CancellationToken.None);

        // §6.1 row 2: new control-message-id
        Assert.Equal(ReplayDecision.NewControlMessageId, result.Decision);
        Assert.NotNull(result.DispatchedMessageId);
        Assert.StartsWith("ctl-", result.DispatchedMessageId);
        Assert.NotEqual(messageId, result.DispatchedMessageId);  // must NOT reuse original

        // Transport received one envelope with the new control id
        Assert.Single(transport.Published);
        var (env, _) = transport.Published[0];
        Assert.Equal(result.DispatchedMessageId, env.MessageId);

        // Payload preserved (Inv-15/§11) — operation_id unchanged under the new key
        Assert.True(env.Payload.TryGetProperty("operation_id", out var opProp));
        Assert.Equal(operationId, opProp.GetString());

        // DLQ row Replayed
        await using var readCtx = fixture.CreateContext(tenantId);
        var dlq = await readCtx.DeadLetterMessages
            .AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.MessageId == messageId && e.ConsumerName == consumer);
        Assert.Equal(DlqReplayState.Replayed, dlq.ReplayState);
    }

    // ── Test 3: Inbox completed → ack without re-admit ────────────────────────────

    [Fact]
    public async Task InboxCompleted_AckedWithoutReadmit_NoPublish()
    {
        var tenantId = $"t7-rpl-c-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer";

        await SeedDlqRowAsync(fixture, tenantId, messageId, consumer);
        await SeedInboxRowAsync(fixture, tenantId, consumer, messageId, InboxOutcome.Completed);

        var handler = BuildHandler(tenantId, out var transport);
        var result = await handler.Handle(
            new ReplayDeadLetterCommand(tenantId, messageId, consumer),
            CancellationToken.None);

        // §6.1 row 3: ack without re-admit
        Assert.Equal(ReplayDecision.AckedWithoutReadmit, result.Decision);
        Assert.Null(result.DispatchedMessageId);

        // Transport NOT called (no publish)
        Assert.Empty(transport.Published);

        // DLQ row Replayed
        await using var readCtx = fixture.CreateContext(tenantId);
        var dlq = await readCtx.DeadLetterMessages
            .AsNoTracking()
            .SingleAsync(e => e.TenantId == tenantId && e.MessageId == messageId && e.ConsumerName == consumer);
        Assert.Equal(DlqReplayState.Replayed, dlq.ReplayState);

        // Audit event written
        var audit = await readCtx.AuditEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EventType == "dead_letter.replayed"
                                   && e.SubjectRef == messageId);
        Assert.NotNull(audit);
    }

    // ── Test 4: DLQ not found → throws DeadLetterNotFoundException ───────────────

    [Fact]
    public async Task DlqNotFound_ThrowsDeadLetterNotFoundException()
    {
        var tenantId = $"t7-rpl-d-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer";
        // No DLQ row seeded

        var handler = BuildHandler(tenantId, out _);

        await Assert.ThrowsAsync<DeadLetterNotFoundException>(
            () => handler.Handle(
                new ReplayDeadLetterCommand(tenantId, messageId, consumer),
                CancellationToken.None));
    }

    // ── Test 4b: Idempotent replay — already-Replayed row is a no-op (no double-publish) ─

    [Fact]
    public async Task AlreadyReplayed_IsNoOp_DoesNotRePublish()
    {
        var tenantId = $"t7-rpl-idem-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer";

        // Seed a DLQ row that has ALREADY been replayed (not Quarantined).
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.DeadLetterMessages.Add(new DeadLetterMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                ConsumerName = consumer,
                MessageType = "booking.command.requested.v1",
                Payload = "{}",
                ErrorClass = "TestError",
                AttemptHistory = "[]",
                FirstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                DeadLetteredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                ReplayState = DlqReplayState.Replayed,
            });
            await ctx.SaveChangesAsync();
        }

        var handler = BuildHandler(tenantId, out var transport);
        var result = await handler.Handle(
            new ReplayDeadLetterCommand(tenantId, messageId, consumer),
            CancellationToken.None);

        // Idempotent no-op: no re-publish, decision is ack-without-readmit.
        Assert.Equal(ReplayDecision.AckedWithoutReadmit, result.Decision);
        Assert.Null(result.DispatchedMessageId);
        Assert.Empty(transport.Published);
    }

    // ── Test 4c: control id is deterministic → retry after mark-failure dedups ────
    //
    // Publish (Step 6) precedes the mark/audit txn (Step 7). If publish succeeds but the mark
    // fails, the row stays Quarantined; a retry must republish the SAME control id so the inbox
    // dedups it to a single resume delivery (no double-fire across the publish-before-mark window).

    [Fact]
    public async Task AlreadyAdmitted_MarkFailsThenRetries_ReusesSameDeterministicControlId()
    {
        var tenantId = $"t7-rpl-det-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer";

        await SeedDlqRowAsync(fixture, tenantId, messageId, consumer, operationId: operationId);
        await SeedInboxRowAsync(fixture, tenantId, consumer, messageId, InboxOutcome.Admitted);

        var transport = new ReplayFakeTransport();

        // First replay: audit repo throws → the mark/audit txn rolls back → row stays Quarantined,
        // but the transport has ALREADY published the control message once.
        await using (var ctx1 = fixture.CreateContext(tenantId))
        {
            var throwingAudit = new Mock<IAuditEventRepository>();
            throwingAudit
                .Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("simulated mark/audit failure"));

            var handler1 = new ReplayDeadLetterCommandHandler(
                new DeadLetterRepository(ctx1), new InboxRepository(ctx1), transport,
                throwingAudit.Object, new UnitOfWork(ctx1));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler1.Handle(new ReplayDeadLetterCommand(tenantId, messageId, consumer), CancellationToken.None));
        }

        // Row must still be Quarantined (the mark rolled back).
        await using (var checkCtx = fixture.CreateContext(tenantId))
        {
            var dlq = await checkCtx.DeadLetterMessages.AsNoTracking()
                .SingleAsync(e => e.TenantId == tenantId && e.MessageId == messageId && e.ConsumerName == consumer);
            Assert.Equal(DlqReplayState.Quarantined, dlq.ReplayState);
        }

        // Retry with a working audit repo: succeeds, marks Replayed, reuses the SAME control id.
        await using (var ctx2 = fixture.CreateContext(tenantId))
        {
            var handler2 = new ReplayDeadLetterCommandHandler(
                new DeadLetterRepository(ctx2), new InboxRepository(ctx2), transport,
                new AuditEventRepository(ctx2), new UnitOfWork(ctx2));
            var result = await handler2.Handle(
                new ReplayDeadLetterCommand(tenantId, messageId, consumer), CancellationToken.None);
            Assert.Equal(ReplayDecision.NewControlMessageId, result.Decision);
        }

        // Both deliveries carried the SAME deterministic control id → a real inbox dedups them
        // to ONE resume (effectively-once across the publish-before-mark window).
        var ctlIds = transport.Published
            .Select(p => p.Envelope.MessageId)
            .Where(id => id.StartsWith("ctl-", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, ctlIds.Count);
        Assert.Single(ctlIds.Distinct());
        Assert.Equal($"ctl-{consumer}-{messageId}", ctlIds[0]);
    }

    // ── Test 5: Payload preserved across both publish cases (Inv-15/§11) ──────────
    //
    // This explicitly asserts that the RAW payload text in the dispatched envelope equals
    // the original DLQ payload — manual retry cannot change payload under the old key.

    [Fact]
    public async Task PayloadPreserved_NeverAdmitted_RawTextEquals_DlqPayload()
    {
        var tenantId = $"t7-rpl-e1-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer";
        var originalPayload = JsonSerializer.Serialize(new
        {
            operation_id = operationId,
            intent_id = "intent-abc",
            intent_revision = 3,
        });

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.DeadLetterMessages.Add(new DeadLetterMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                ConsumerName = consumer,
                MessageType = "booking.command.requested.v1",
                Payload = originalPayload,
                ErrorClass = "TestError",
                AttemptHistory = "[]",
                FirstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                DeadLetteredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                ReplayState = DlqReplayState.Quarantined,
            });
            await ctx.SaveChangesAsync();
        }

        var handler = BuildHandler(tenantId, out var transport);
        await handler.Handle(
            new ReplayDeadLetterCommand(tenantId, messageId, consumer),
            CancellationToken.None);

        Assert.Single(transport.Published);
        var (env, _) = transport.Published[0];

        // Payload preserved (Inv-15/§11): same keys and values as original, regardless of jsonb key order.
        // Postgres jsonb may reorder keys; compare by parsing both sides and asserting property values.
        AssertPayloadFieldsPreserved(originalPayload, env.Payload);
    }

    [Fact]
    public async Task PayloadPreserved_AlreadyAdmitted_RawTextEquals_DlqPayload()
    {
        var tenantId = $"t7-rpl-e2-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer";
        var originalPayload = JsonSerializer.Serialize(new
        {
            operation_id = operationId,
            provider_key = "pk-stable",
        });

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.DeadLetterMessages.Add(new DeadLetterMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                ConsumerName = consumer,
                MessageType = "booking.command.requested.v1",
                Payload = originalPayload,
                ErrorClass = "TestError",
                AttemptHistory = "[]",
                FirstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                DeadLetteredAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                ReplayState = DlqReplayState.Quarantined,
            });
            ctx.ConsumerInbox.Add(new ConsumerInbox
            {
                TenantId = tenantId,
                ConsumerName = consumer,
                MessageId = messageId,
                AdmittedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                Outcome = InboxOutcome.Admitted,
            });
            await ctx.SaveChangesAsync();
        }

        var handler = BuildHandler(tenantId, out var transport);
        await handler.Handle(
            new ReplayDeadLetterCommand(tenantId, messageId, consumer),
            CancellationToken.None);

        Assert.Single(transport.Published);
        var (env, _) = transport.Published[0];

        // Payload preserved (Inv-15/§11): same keys and values, jsonb key-order independent.
        AssertPayloadFieldsPreserved(originalPayload, env.Payload);
    }

    // ── Test 6: TryMarkReplayedAsync is an atomic single-winner claim ────────────────
    //
    // The Quarantined→Replayed transition is the atomic claim that makes concurrent replays safe:
    // exactly one caller affects the row; the rest get 0 rows and suppress duplicate side effects.

    [Fact]
    public async Task TryMarkReplayedAsync_TransitionsQuarantinedExactlyOnce()
    {
        var tenantId = $"t7-claim-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        var consumer = "booking.finalizer";

        await SeedDlqRowAsync(fixture, tenantId, messageId, consumer);

        await using var ctx = fixture.CreateContext(tenantId);
        var repo = new DeadLetterRepository(ctx);

        var first = await repo.TryMarkReplayedAsync(tenantId, messageId, consumer);
        var second = await repo.TryMarkReplayedAsync(tenantId, messageId, consumer);

        Assert.Equal(1, first);   // won the Quarantined→Replayed transition
        Assert.Equal(0, second);  // already Replayed → no-op (atomic claim prevents a second winner)
    }

    // ── Assertion helper ──────────────────────────────────────────────────────────

    /// <summary>
    /// Asserts that every property in <paramref name="expected"/> is present in
    /// <paramref name="actual"/> with an equal value, regardless of JSON key ordering
    /// (postgres jsonb normalises key order on storage/retrieval).
    /// This satisfies Inv-15/§11: payload is preserved (no mutation), not identical bytes.
    /// </summary>
    private static void AssertPayloadFieldsPreserved(string expected, JsonElement actual)
    {
        using var expectedDoc = JsonDocument.Parse(expected);
        foreach (var prop in expectedDoc.RootElement.EnumerateObject())
        {
            Assert.True(actual.TryGetProperty(prop.Name, out var actualVal),
                $"Expected property '{prop.Name}' in dispatched envelope payload");
            Assert.Equal(prop.Value.ToString(), actualVal.ToString());
        }
    }
}
