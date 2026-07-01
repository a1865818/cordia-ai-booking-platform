#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using System.Text.Json;
using Cordia.Application.Handlers.Preorder;
using Cordia.Application.Handlers.Preorder.StartSaga;
using Cordia.Application.Services;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cordia.Tests.IntegrationTests.Preorder;

/// <summary>
/// Spec 11 §5.3 Phase 3 — live item revalidation at saga start (Inv-10). A saga whose pre-order
/// lines are all still published at the captured price advances Revalidating → HoldingCapacity and
/// enqueues the hold step; a saga with an unpublished item or a changed price resolves to
/// FailedFinal, emits an AlternativesRequired outcome, audits the failure, and takes NO hold.
/// Re-delivery (a saga already exists for the operation) is an idempotent no-op.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class PreorderRevalidationTests(PostgresFixture fixture)
{
    private static StartPreorderSagaCommandHandler BuildStart(Cordia.Infrastructure.EntityFramework.ApplicationDbContext ctx)
        => new(
            new PreorderSagaRepository(ctx),
            new LiveItemRevalidationService(new VendorCatalogRepository(ctx)),
            new OutboxRepository(ctx),
            new AuditEventRepository(ctx),
            new ScheduledJobRepository(ctx), NullLogger<StartPreorderSagaCommandHandler>.Instance);

    private async Task<string> SeedPublishedItemAsync(
        string tenantId, string itemId, long priceMinor, string currency = "AUD", string? modifiersJson = null)
    {
        var catalogId = $"cat-{Guid.NewGuid():N}";
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.VendorCatalogs.Add(new VendorCatalog
        {
            TenantId = tenantId,
            CatalogId = catalogId,
            VersionLabel = "menu_pre",
            Status = CatalogStatus.Published,
            SourceHash = "sha256-x",
        });
        ctx.VendorCatalogItems.Add(new VendorCatalogItem
        {
            TenantId = tenantId,
            ItemId = itemId,
            CatalogId = catalogId,
            Name = itemId,
            Description = "d",
            PriceMinor = priceMinor,
            Currency = currency,
            Modifiers = modifiersJson,
        });
        await ctx.SaveChangesAsync();
        return catalogId;
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
            Version = 0,
        });
        await ctx.SaveChangesAsync();
        return operationId;
    }

    private static string Reference(string itemId, long priceMinor, string currency = "AUD", string modifierIds = "[]")
        => $$"""
            { "catalog_version": "menu_pre", "confirmation_id": "conf_1",
              "lines": [ { "item_id": "{{itemId}}", "modifier_ids": {{modifierIds}}, "quantity": 2,
                           "captured_price_amount_minor": {{priceMinor}}, "captured_price_currency": "{{currency}}" } ] }
            """;

    private async Task RunStartAsync(string tenantId, string operationId, string referenceJson)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = BuildStart(ctx);
        await using var tx = await ctx.Database.BeginTransactionAsync();
        await handler.Handle(
            new StartPreorderSagaCommand(tenantId, $"msg-{Guid.NewGuid():N}", null, operationId, referenceJson, Standalone: false),
            CancellationToken.None);
        await tx.CommitAsync();
    }

    [Fact]
    public async Task Start_AllItemsValid_AdvancesToReservingItems_AndEnqueuesReserve()
    {
        var tenantId = $"pre-reval-ok-{Guid.NewGuid():N}";
        await SeedPublishedItemAsync(tenantId, "item_1", priceMinor: 1500);
        var operationId = await SeedOperationAsync(tenantId);

        await RunStartAsync(tenantId, operationId, Reference("item_1", 1500));

        await using var ctx = fixture.CreateContext(tenantId);
        var saga = await ctx.PreorderSagas.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(s => s.TenantId == tenantId && s.OperationId == operationId);
        // Items-first for every pre-order (§5.3): the saga no longer holds capacity itself.
        Assert.Equal(PreorderSagaState.ReservingItems, saga.SagaState);

        Assert.True(await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.MessageType == PreorderSagaMessages.ReserveItems));
        Assert.True(await ctx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(a => a.TenantId == tenantId && a.EventType == "preorder.revalidated"));
    }

    [Fact]
    public async Task Start_ItemUnpublished_ResolvesFailedFinal_EmitsAlternatives_NoHold()
    {
        var tenantId = $"pre-reval-gone-{Guid.NewGuid():N}";
        // item_1 is NOT seeded as published.
        var operationId = await SeedOperationAsync(tenantId);

        await RunStartAsync(tenantId, operationId, Reference("item_1", 1500));

        await using var ctx = fixture.CreateContext(tenantId);
        var saga = await ctx.PreorderSagas.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(s => s.TenantId == tenantId && s.OperationId == operationId);
        Assert.Equal(PreorderSagaState.FailedFinal, saga.SagaState);

        Assert.True(await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.MessageType == StartPreorderSagaCommandHandler.AlternativesRequiredEventType));
        Assert.False(await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.MessageType == PreorderSagaMessages.ReserveItems));
        Assert.True(await ctx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(a => a.TenantId == tenantId && a.EventType == "preorder.revalidation_failed"));
    }

    [Fact]
    public async Task Start_PriceChanged_ResolvesFailedFinal()
    {
        var tenantId = $"pre-reval-price-{Guid.NewGuid():N}";
        await SeedPublishedItemAsync(tenantId, "item_1", priceMinor: 1800); // live price differs
        var operationId = await SeedOperationAsync(tenantId);

        await RunStartAsync(tenantId, operationId, Reference("item_1", 1500)); // captured 1500

        await using var ctx = fixture.CreateContext(tenantId);
        var saga = await ctx.PreorderSagas.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(s => s.TenantId == tenantId && s.OperationId == operationId);
        Assert.Equal(PreorderSagaState.FailedFinal, saga.SagaState);
    }

    [Fact]
    public async Task Start_OnExistingSaga_IsIdempotentNoop()
    {
        var tenantId = $"pre-reval-idem-{Guid.NewGuid():N}";
        await SeedPublishedItemAsync(tenantId, "item_1", priceMinor: 1500);
        var operationId = await SeedOperationAsync(tenantId);

        await RunStartAsync(tenantId, operationId, Reference("item_1", 1500));
        await RunStartAsync(tenantId, operationId, Reference("item_1", 1500)); // second start

        await using var ctx = fixture.CreateContext(tenantId);
        var count = await ctx.PreorderSagas.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(s => s.TenantId == tenantId && s.OperationId == operationId);
        Assert.Equal(1, count); // only one saga
    }
}
