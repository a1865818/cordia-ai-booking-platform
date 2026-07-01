#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Application.Handlers.Preorder;
using Cordia.Application.Handlers.Preorder.BookingSignal;
using Cordia.Application.Handlers.Preorder.CommitPreorder;
using Cordia.Application.Handlers.Preorder.ReserveItems;
using Cordia.Application.Handlers.Preorder.StartSaga;
using Cordia.Application.Services;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Providers;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cordia.Tests.IntegrationTests.Preorder;

/// <summary>
/// Spec 11 §5.3 (divergence #2) — the booking-delegated forward saga drives a pre-order from
/// Revalidating to Completed: revalidate → reserve items → <b>hand the whole booking to the spec-06
/// engine</b> (capacity/payment/create) and wait in CommittingBooking → on the 06 <c>booking_committed</c>
/// signal advance to CommittingPreorder → commit the pre-order → Completed, emitting exactly one
/// combined-outcome notification (Inv-8). The 06 engine is simulated here by invoking the
/// <see cref="PreorderBookingCommittedCommandHandler"/> (the signal consumer); the 06 pipeline itself
/// owns capacity finalization and is covered by its own integration tests. Re-delivery of the terminal
/// commit is an idempotent no-op (Inv-5/Inv-9).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class PreorderSagaHappyPathTests(PostgresFixture fixture)
{
    private static StartPreorderSagaCommandHandler Start(ApplicationDbContext ctx)
        => new(new PreorderSagaRepository(ctx), new LiveItemRevalidationService(new VendorCatalogRepository(ctx)),
            new OutboxRepository(ctx), new AuditEventRepository(ctx), new ScheduledJobRepository(ctx), NullLogger<StartPreorderSagaCommandHandler>.Instance);

    private static ReservePreorderItemsCommandHandler Reserve(ApplicationDbContext ctx)
        => new(new PreorderSagaRepository(ctx), new StubPreorderItemReservation(),
            new OutboxRepository(ctx), new AuditEventRepository(ctx), new ScheduledJobRepository(ctx), NullLogger<ReservePreorderItemsCommandHandler>.Instance);

    /// <summary>Simulates the spec-06 engine signalling the saga that the booking committed.</summary>
    private static PreorderBookingCommittedCommandHandler BookingCommitted(ApplicationDbContext ctx)
        => new(new PreorderSagaRepository(ctx), new OutboxRepository(ctx), new AuditEventRepository(ctx),
            new ScheduledJobRepository(ctx), NullLogger<PreorderBookingCommittedCommandHandler>.Instance);

    private static CommitPreorderCommandHandler Commit(ApplicationDbContext ctx)
        => new(new PreorderSagaRepository(ctx), new OutboxRepository(ctx), new AuditEventRepository(ctx),
            NullLogger<CommitPreorderCommandHandler>.Instance);

    private async Task InTxAsync(string tenantId, Func<ApplicationDbContext, Task> body)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        await using var tx = await ctx.Database.BeginTransactionAsync();
        await body(ctx);
        await tx.CommitAsync();
    }

    private async Task SeedItemAsync(string tenantId, string itemId, long priceMinor)
    {
        var catalogId = $"cat-{Guid.NewGuid():N}";
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.VendorCatalogs.Add(new VendorCatalog
        {
            TenantId = tenantId,
            CatalogId = catalogId,
            VersionLabel = "menu_pre",
            Status = CatalogStatus.Published,
            SourceHash = "x",
        });
        ctx.VendorCatalogItems.Add(new VendorCatalogItem
        {
            TenantId = tenantId,
            ItemId = itemId,
            CatalogId = catalogId,
            Name = itemId,
            Description = "d",
            PriceMinor = priceMinor,
            Currency = "AUD",
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<string> SeedOperationAsync(string tenantId)
    {
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        await using var ctx = fixture.CreateContext(tenantId);
        var intent = new BookingIntent { TenantId = tenantId, IntentId = intentId, OperationType = OperationTypeEnum.Create };
        ctx.BookingIntents.Add(intent);
        ctx.BookingIntentRevisions.Add(new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = "{}",
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
            Intent = intent,
        });
        ctx.BookingOperations.Add(new BookingOperation
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = OperationStatus.Accepted,
            IsPreorder = true,
            Version = 0,
        });
        await ctx.SaveChangesAsync();
        return operationId;
    }

    private static string Reference(string itemId, long priceMinor, int qty)
        => $$"""
            { "catalog_version": "menu_pre", "confirmation_id": "conf_1",
              "lines": [ { "item_id": "{{itemId}}", "modifier_ids": [], "quantity": {{qty}},
                           "captured_price_amount_minor": {{priceMinor}}, "captured_price_currency": "AUD" } ] }
            """;

    private async Task<PreorderSaga> LoadSagaAsync(string tenantId, string operationId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        return await ctx.PreorderSagas.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(s => s.TenantId == tenantId && s.OperationId == operationId);
    }

    private async Task DriveToCommittingBookingAsync(string tenantId, string operationId, string sagaId)
    {
        await InTxAsync(tenantId, ctx => Reserve(ctx).Handle(
            new ReservePreorderItemsCommand(tenantId, $"m-{Guid.NewGuid():N}", null, sagaId, operationId), CancellationToken.None));
    }

    [Fact]
    public async Task ForwardSaga_DrivesToCompleted_ViaSpec06Signal_WithSingleOutcome()
    {
        var tenantId = $"pre-hp-{Guid.NewGuid():N}";
        await SeedItemAsync(tenantId, "item_1", 1500);
        var operationId = await SeedOperationAsync(tenantId);

        // Start → revalidate ok → ReservingItems (items-first for every pre-order).
        await InTxAsync(tenantId, ctx => Start(ctx).Handle(
            new StartPreorderSagaCommand(tenantId, $"m-{Guid.NewGuid():N}", null, operationId,
                Reference("item_1", 1500, 2), Standalone: false, SlotId: null, RequestedCapacity: 2), CancellationToken.None));

        var saga = await LoadSagaAsync(tenantId, operationId);
        var sagaId = saga.SagaId;
        Assert.Equal(PreorderSagaState.ReservingItems, saga.SagaState);

        // Reserve items → CommittingBooking; the saga hands the booking to the 06 engine.
        await DriveToCommittingBookingAsync(tenantId, operationId, sagaId);
        Assert.Equal(PreorderSagaState.CommittingBooking, (await LoadSagaAsync(tenantId, operationId)).SagaState);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            Assert.True(await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(m => m.TenantId == tenantId && m.OperationId == operationId
                    && m.MessageType == "booking.command.requested.v1"));
        }

        // Simulate the 06 engine's booking_committed signal → CommittingPreorder.
        await InTxAsync(tenantId, ctx => BookingCommitted(ctx).Handle(
            new PreorderBookingCommittedCommand(tenantId, $"m-{Guid.NewGuid():N}", null, operationId), CancellationToken.None));
        Assert.Equal(PreorderSagaState.CommittingPreorder, (await LoadSagaAsync(tenantId, operationId)).SagaState);

        // Commit pre-order → Completed.
        await InTxAsync(tenantId, ctx => Commit(ctx).Handle(
            new CommitPreorderCommand(tenantId, $"m-{Guid.NewGuid():N}", null, sagaId, operationId), CancellationToken.None));

        await using var assertCtx = fixture.CreateContext(tenantId);
        var final = await assertCtx.PreorderSagas.AsNoTracking().IgnoreQueryFilters().FirstAsync(s => s.SagaId == sagaId);
        Assert.Equal(PreorderSagaState.Completed, final.SagaState);

        // Exactly one combined success outcome (Inv-8).
        var confirmations = await assertCtx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tenantId && m.MessageType == CommitPreorderCommandHandler.OutcomeConfirmedEventType);
        Assert.Equal(1, confirmations);
    }

    [Fact]
    public async Task CommitPreorder_RedeliveredAfterCompletion_IsIdempotent()
    {
        var tenantId = $"pre-idem-{Guid.NewGuid():N}";
        await SeedItemAsync(tenantId, "item_1", 1500);
        var operationId = await SeedOperationAsync(tenantId);

        await InTxAsync(tenantId, ctx => Start(ctx).Handle(
            new StartPreorderSagaCommand(tenantId, $"m-{Guid.NewGuid():N}", null, operationId,
                Reference("item_1", 1500, 2), Standalone: false, SlotId: null, RequestedCapacity: 2), CancellationToken.None));
        var sagaId = (await LoadSagaAsync(tenantId, operationId)).SagaId;
        await DriveToCommittingBookingAsync(tenantId, operationId, sagaId);
        await InTxAsync(tenantId, ctx => BookingCommitted(ctx).Handle(new PreorderBookingCommittedCommand(tenantId, $"m-{Guid.NewGuid():N}", null, operationId), CancellationToken.None));
        await InTxAsync(tenantId, ctx => Commit(ctx).Handle(new CommitPreorderCommand(tenantId, $"m-{Guid.NewGuid():N}", null, sagaId, operationId), CancellationToken.None));
        // Duplicate delivery of commit.
        await InTxAsync(tenantId, ctx => Commit(ctx).Handle(new CommitPreorderCommand(tenantId, $"m-{Guid.NewGuid():N}", null, sagaId, operationId), CancellationToken.None));

        await using var assertCtx = fixture.CreateContext(tenantId);
        var confirmations = await assertCtx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tenantId && m.MessageType == CommitPreorderCommandHandler.OutcomeConfirmedEventType);
        Assert.Equal(1, confirmations); // still exactly one — no double notify
    }
}
