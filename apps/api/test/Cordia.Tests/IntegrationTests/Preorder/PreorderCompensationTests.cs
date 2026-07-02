#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Application.Handlers.Preorder;
using Cordia.Application.Handlers.Preorder.BookingSignal;
using Cordia.Application.Handlers.Preorder.CompensateBooking;
using Cordia.Application.Handlers.Preorder.CompensateItems;
using Cordia.Application.Handlers.Preorder.Reconcile;
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
/// Spec 11 §5.3 (divergence #2/#3) — compensation + reconciliation under the booking-delegated design.
/// The saga owns only the item reservation, so every failure path unwinds items and ends FailedFinal
/// with exactly one failure outcome (Inv-8): items-unavailable at reserve time, and a spec-06
/// booking-failure signal both route CommittingBooking/ReservingItems → CompensatingItems → FailedFinal.
/// A post-commit failure (the now-unreachable CompensatingBooking) escalates to ManualReview rather
/// than auto-reversing the committed booking (§21.4). Reconcile re-drives a stuck saga idempotently: a
/// saga waiting on the 06 engine is re-nudged with <c>booking.command.requested.v1</c> (Inv-9).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class PreorderCompensationTests(PostgresFixture fixture)
{
    private static StartPreorderSagaCommandHandler Start(ApplicationDbContext ctx)
        => new(new PreorderSagaRepository(ctx), new BookingOperationRepository(ctx),
            new LiveItemRevalidationService(new VendorCatalogRepository(ctx)),
            new OutboxRepository(ctx), new AuditEventRepository(ctx), new ScheduledJobRepository(ctx), NullLogger<StartPreorderSagaCommandHandler>.Instance);
    private static ReservePreorderItemsCommandHandler Reserve(ApplicationDbContext ctx)
        => new(new PreorderSagaRepository(ctx), new StubPreorderItemReservation(),
            new OutboxRepository(ctx), new AuditEventRepository(ctx), new ScheduledJobRepository(ctx), NullLogger<ReservePreorderItemsCommandHandler>.Instance);
    /// <summary>Simulates the spec-06 engine signalling the saga that the booking failed.</summary>
    private static PreorderBookingFailedCommandHandler BookingFailed(ApplicationDbContext ctx)
        => new(new PreorderSagaRepository(ctx), new OutboxRepository(ctx), new AuditEventRepository(ctx),
            NullLogger<PreorderBookingFailedCommandHandler>.Instance);
    private static CompensatePreorderItemsCommandHandler CompItems(ApplicationDbContext ctx)
        => new(new PreorderSagaRepository(ctx), new StubPreorderItemReservation(),
            new OutboxRepository(ctx), new AuditEventRepository(ctx), NullLogger<CompensatePreorderItemsCommandHandler>.Instance);
    private static CompensatePreorderBookingCommandHandler CompBooking(ApplicationDbContext ctx)
        => new(new PreorderSagaRepository(ctx), new OutboxRepository(ctx), new AuditEventRepository(ctx),
            NullLogger<CompensatePreorderBookingCommandHandler>.Instance);
    private static ReconcilePreorderSagaCommandHandler Reconcile(ApplicationDbContext ctx)
        => new(new PreorderSagaRepository(ctx), new BookingOperationRepository(ctx),
            new OutboxRepository(ctx), new AuditEventRepository(ctx),
            NullLogger<ReconcilePreorderSagaCommandHandler>.Instance);

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
        ctx.VendorCatalogs.Add(new VendorCatalog { TenantId = tenantId, CatalogId = catalogId, VersionLabel = "menu_pre", Status = CatalogStatus.Published, SourceHash = "x" });
        ctx.VendorCatalogItems.Add(new VendorCatalogItem { TenantId = tenantId, ItemId = itemId, CatalogId = catalogId, Name = itemId, Description = "d", PriceMinor = priceMinor, Currency = "AUD" });
        await ctx.SaveChangesAsync();
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

    private static string Reference(string itemId, long priceMinor)
        => $$"""
            { "catalog_version": "menu_pre", "confirmation_id": "conf_1",
              "lines": [ { "item_id": "{{itemId}}", "modifier_ids": [], "quantity": 2,
                           "captured_price_amount_minor": {{priceMinor}}, "captured_price_currency": "AUD" } ] }
            """;

    private async Task<PreorderSaga> LoadAsync(string tenantId, string operationId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        return await ctx.PreorderSagas.AsNoTracking().IgnoreQueryFilters().FirstAsync(s => s.TenantId == tenantId && s.OperationId == operationId);
    }

    /// <summary>Start → reserve items; returns the saga id. With a "reserve_fail" item the reserve routes to CompensatingItems.</summary>
    private async Task<string> StartReserveAsync(string tenantId, string operationId, string itemId, long price)
    {
        await InTxAsync(tenantId, ctx => Start(ctx).Handle(new StartPreorderSagaCommand(tenantId, $"m-{Guid.NewGuid():N}", null, operationId, Reference(itemId, price)), CancellationToken.None));
        var sagaId = (await LoadAsync(tenantId, operationId)).SagaId;
        await InTxAsync(tenantId, ctx => Reserve(ctx).Handle(new ReservePreorderItemsCommand(tenantId, $"m-{Guid.NewGuid():N}", null, sagaId, operationId), CancellationToken.None));
        return sagaId;
    }

    private async Task AssertOneFailureOutcomeAsync(string tenantId)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var failures = await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tenantId && m.MessageType == PreorderSagaSupport.OutcomeFailedEventType);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task ItemsUnavailable_CompensatesItems_ToFailedFinal()
    {
        var tenantId = $"pre-comp-items-{Guid.NewGuid():N}";
        await SeedItemAsync(tenantId, "reserve_fail_1", 1500); // published, but the stub refuses to reserve it
        var operationId = await SeedOperationAsync(tenantId);

        var sagaId = await StartReserveAsync(tenantId, operationId, "reserve_fail_1", 1500);
        Assert.Equal(PreorderSagaState.CompensatingItems, (await LoadAsync(tenantId, operationId)).SagaState);

        await InTxAsync(tenantId, ctx => CompItems(ctx).Handle(new CompensatePreorderItemsCommand(tenantId, $"m-{Guid.NewGuid():N}", null, sagaId, operationId), CancellationToken.None));

        Assert.Equal(PreorderSagaState.FailedFinal, (await LoadAsync(tenantId, operationId)).SagaState);
        await AssertOneFailureOutcomeAsync(tenantId);
    }

    [Fact]
    public async Task Spec06BookingFailed_CompensatesItems_ToFailedFinal()
    {
        var tenantId = $"pre-comp-06fail-{Guid.NewGuid():N}";
        await SeedItemAsync(tenantId, "item_1", 1500);
        var operationId = await SeedOperationAsync(tenantId);

        var sagaId = await StartReserveAsync(tenantId, operationId, "item_1", 1500);
        Assert.Equal(PreorderSagaState.CommittingBooking, (await LoadAsync(tenantId, operationId)).SagaState);

        // 06 signals booking failure → CompensatingItems.
        await InTxAsync(tenantId, ctx => BookingFailed(ctx).Handle(new PreorderBookingFailedCommand(tenantId, $"m-{Guid.NewGuid():N}", null, operationId), CancellationToken.None));
        Assert.Equal(PreorderSagaState.CompensatingItems, (await LoadAsync(tenantId, operationId)).SagaState);

        await InTxAsync(tenantId, ctx => CompItems(ctx).Handle(new CompensatePreorderItemsCommand(tenantId, $"m-{Guid.NewGuid():N}", null, sagaId, operationId), CancellationToken.None));
        Assert.Equal(PreorderSagaState.FailedFinal, (await LoadAsync(tenantId, operationId)).SagaState);
        await AssertOneFailureOutcomeAsync(tenantId);
    }

    [Fact]
    public async Task CompensateBooking_PostCommitFailure_EscalatesToManualReview()
    {
        // CompensatingBooking is unreachable in the normal flow; seed it directly to exercise the
        // defensive handler that preserves the committed booking (§21.4) and parks for a human.
        var tenantId = $"pre-comp-mr-{Guid.NewGuid():N}";
        var operationId = await SeedOperationAsync(tenantId);
        var sagaId = $"pos_{Guid.NewGuid():N}";
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.PreorderSagas.Add(new PreorderSaga
            {
                TenantId = tenantId,
                SagaId = sagaId,
                OperationId = operationId,
                SagaState = PreorderSagaState.CompensatingBooking,
                PreorderReference = Reference("item_1", 1500),
                IsStandalone = false,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await InTxAsync(tenantId, ctx => CompBooking(ctx).Handle(new CompensatePreorderBookingCommand(tenantId, $"m-{Guid.NewGuid():N}", null, sagaId, operationId), CancellationToken.None));

        var saga = await LoadAsync(tenantId, operationId);
        Assert.Equal(PreorderSagaState.ManualReview, saga.SagaState);

        // Inv-8: the park emits ONLY a neutral "under review" notice — NOT a terminal failure outcome
        // (the booking is committed). The single combined outcome is owned by the terminal convert/cancel.
        await using var ctx2 = fixture.CreateContext(tenantId);
        var inReview = await ctx2.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tenantId && m.MessageType == PreorderSagaMessages.InReviewNotification);
        Assert.Equal(1, inReview);
        var failures = await ctx2.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tenantId && m.MessageType == PreorderSagaSupport.OutcomeFailedEventType);
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task Reconcile_OnStuckCommittingBooking_ReNudgesSpec06_Idempotently()
    {
        var tenantId = $"pre-recon-{Guid.NewGuid():N}";
        await SeedItemAsync(tenantId, "item_1", 1500);
        var operationId = await SeedOperationAsync(tenantId);

        var sagaId = await StartReserveAsync(tenantId, operationId, "item_1", 1500);
        Assert.Equal(PreorderSagaState.CommittingBooking, (await LoadAsync(tenantId, operationId)).SagaState); // waiting on 06

        await InTxAsync(tenantId, ctx => Reconcile(ctx).Handle(new ReconcilePreorderSagaCommand(tenantId, $"m-{Guid.NewGuid():N}", null, sagaId, operationId), CancellationToken.None));

        await using var ctx2 = fixture.CreateContext(tenantId);
        // State unchanged; the 06 engine was re-nudged for idempotent re-entry.
        Assert.Equal(PreorderSagaState.CommittingBooking, (await ctx2.PreorderSagas.AsNoTracking().IgnoreQueryFilters().FirstAsync(s => s.SagaId == sagaId)).SagaState);
        Assert.True(await ctx2.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.OperationId == operationId && m.MessageType == "booking.command.requested.v1"));
        Assert.True(await ctx2.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(a => a.TenantId == tenantId && a.EventType == "preorder.reconcile_resumed"));
    }
}
