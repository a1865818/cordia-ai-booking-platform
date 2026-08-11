#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Application.Handlers.Preorder;
using Cordia.Application.Handlers.Preorder.Reconcile;
using Cordia.Application.Handlers.Preorder.Recovery;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cordia.Tests.IntegrationTests.Preorder;

/// <summary>
/// Spec 11 §5.3 (divergence #2/#3) — crash recovery (FM#8-11). The recovery sweep re-drives a saga
/// stuck mid-step at its CURRENT step; a saga waiting on the spec-06 engine (CommittingBooking) is
/// re-nudged with <c>booking.command.requested.v1</c> without mutating its checkpoints (Inv-9). The
/// 06 driver is idempotent and re-signals its terminal outcome if the operation already finished.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class PreorderRecoveryAndStandaloneTests(PostgresFixture fixture)
{
    private static ReconcilePreorderSagaCommandHandler ReconcileH(ApplicationDbContext ctx)
        => new(new PreorderSagaRepository(ctx), new BookingOperationRepository(ctx),
            new OutboxRepository(ctx), new AuditEventRepository(ctx),
            new OperationWorkItemRepository(ctx),
            NullLogger<ReconcilePreorderSagaCommandHandler>.Instance);

    private async Task InTxAsync(string tenantId, Func<ApplicationDbContext, Task> body)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        await using var tx = await ctx.Database.BeginTransactionAsync();
        await body(ctx);
        await tx.CommitAsync();
    }

    private async Task<string> SeedOperationAsync(string tenantId)
    {
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        await using var ctx = fixture.CreateContext(tenantId);
        var intent = new BookingIntent { TenantId = tenantId, IntentId = intentId, OperationType = OperationTypeEnum.Create };
        ctx.BookingIntents.Add(intent);
        ctx.BookingIntentRevisions.Add(new BookingIntentRevision { TenantId = tenantId, IntentId = intentId, Revision = 1, CanonicalPayload = "{}", PayloadHash = "h", SchemaVersion = "1.0", TenantPolicyVersion = "1.0", Intent = intent });
        ctx.BookingOperations.Add(new BookingOperation { TenantId = tenantId, OperationId = operationId, IntentId = intentId, IntentRevision = 1, OperationType = OperationTypeEnum.Create, Status = OperationStatus.Accepted, IsPreorder = true, Version = 0 });
        await ctx.SaveChangesAsync();
        return operationId;
    }

    private static string Reference(string itemId, long price)
        => $$"""
            { "catalog_version": "menu_pre", "confirmation_id": "conf_1",
              "lines": [ { "item_id": "{{itemId}}", "modifier_ids": [], "quantity": 1,
                           "captured_price_amount_minor": {{price}}, "captured_price_currency": "AUD" } ] }
            """;

    [Fact(Skip = "Standalone (§21.4) order-only pre-order conversion is not yet designed for the " +
                 "booking-delegated model (divergence #2): the reserve-items step now always hands off to " +
                 "the spec-06 engine (booking.command.requested.v1), which is wrong for an order with no " +
                 "booking. Standalone is also never produced today (SubmitConfirmedIntent always sets " +
                 "standalone=false). Re-author once the standalone path has its own §21.4 flow.")]
    public void StandaloneOrder_Conversion_NeedsBookingDelegatedRedesign() { }

    [Fact]
    public async Task RecoverySweep_FindsStuckSaga_EnqueuesReconcile_WhichRenudges06_NoCheckpointMutation()
    {
        var tenantId = $"pre-recovery-{Guid.NewGuid():N}";
        var operationId = await SeedOperationAsync(tenantId);

        // Seed a saga crashed mid-CommittingBooking: it already handed off to 06 and is waiting.
        var sagaId = $"pos_{Guid.NewGuid():N}";
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.PreorderSagas.Add(new PreorderSaga
            {
                TenantId = tenantId,
                SagaId = sagaId,
                OperationId = operationId,
                SagaState = PreorderSagaState.CommittingBooking,
                PreorderReference = Reference("item_1", 1500),
                CreatedAt = DateTimeOffset.UtcNow.AddYears(-1),
                // Stale, and deliberately the OLDEST stale saga in the database.
                //
                // `FindStuckAsync` orders by `UpdatedAt` ascending and takes a bounded batch
                // (500). The integration database is shared and never pruned, so every previous
                // run leaves its own stale saga behind; once that backlog reached the batch size a
                // saga staled by only an hour fell outside the window and this test failed on a
                // sweep that had worked perfectly — a false failure about accumulated fixtures,
                // not about recovery. Ageing it past every plausible leftover makes the assertion
                // depend on the sweeper's behaviour rather than on the row count of the box it
                // runs on.
                UpdatedAt = DateTimeOffset.UtcNow.AddYears(-1),
                Version = 3,
            });
            await ctx.SaveChangesAsync();
        }

        // Sweep finds it and enqueues reconcile.
        int swept;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var sweeper = new PreorderRecoverySweeper(new PreorderSagaRepository(ctx), new OutboxRepository(ctx));
            swept = await sweeper.SweepAsync(DateTimeOffset.UtcNow.AddMinutes(-30), CancellationToken.None);
        }
        Assert.True(swept >= 1);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            Assert.True(await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(m => m.TenantId == tenantId && m.MessageType == PreorderSagaMessages.Reconcile && m.OperationId == operationId));
        }

        // Reconcile re-nudges the 06 engine for the CURRENT step — no state change, no re-acquisition.
        await InTxAsync(tenantId, ctx => ReconcileH(ctx).Handle(new ReconcilePreorderSagaCommand(tenantId, $"m-{Guid.NewGuid():N}", null, sagaId, operationId), CancellationToken.None));

        await using var assertCtx = fixture.CreateContext(tenantId);
        var saga = await assertCtx.PreorderSagas.AsNoTracking().IgnoreQueryFilters().FirstAsync(s => s.SagaId == sagaId);
        Assert.Equal(PreorderSagaState.CommittingBooking, saga.SagaState); // unchanged
        // The 06 engine is re-nudged (idempotent); the saga is not advanced or re-checkpointed here.
        Assert.True(await assertCtx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.OperationId == operationId && m.MessageType == "booking.command.requested.v1"));
        Assert.True(await assertCtx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(a => a.TenantId == tenantId && a.EventType == "preorder.reconcile_resumed"));
    }
}
