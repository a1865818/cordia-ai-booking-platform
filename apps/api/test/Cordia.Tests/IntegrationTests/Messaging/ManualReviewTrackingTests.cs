#pragma warning disable xUnit1051
using Cordia.Application.Handlers.Console.ListOpenWorkItems;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// ManualReview must be a tracked state, not a hole things fall into.
///
/// <para>Entering it was already well built — <c>ManualReviewEscalator</c> writes the transition, an
/// open work item, a "still confirming" notice and an audit event in one transaction. What was
/// missing was on either side of that: nothing could ENUMERATE the outstanding items, and the
/// pre-order saga's own ManualReview park created no work item at all. An escalation that is
/// durable but unreadable is not meaningfully different from one that was never recorded.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ManualReviewTrackingTests(PostgresFixture fixture)
{
    private static async Task SeedOperationAsync(
        ApplicationDbContext ctx, string tenantId, string operationId, OperationStatus status)
    {
        var intentId = TestIds.New("int");

        ctx.BookingIntents.Add(new BookingIntent
        {
            TenantId = tenantId,
            IntentId = intentId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        ctx.BookingIntentRevisions.Add(new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = """{"operation":"create"}""",
            PayloadHash = TestIds.New("h"),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        ctx.BookingOperations.Add(new BookingOperation
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = status,
            Version = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await ctx.SaveChangesAsync();
    }

    private static OperationWorkItem OpenItem(
        string tenantId, string operationId, string reason, DateTimeOffset slaDeadline)
        => new()
        {
            TenantId = tenantId,
            WorkItemId = TestIds.New("wi"),
            OperationId = operationId,
            Reason = reason,
            State = "open",
            SlaDeadline = slaDeadline,
            CreatedAt = DateTimeOffset.UtcNow,
            Version = 0,
        };

    /// <summary>
    /// Outstanding items can be identified — the gap that made ManualReview a dead end.
    ///
    /// <para>Before this query the table had exactly two readers, both cross-tenant aggregates
    /// feeding metrics gauges. An operator could be told the manual-review depth was rising and had
    /// no way to find out what was in it.</para>
    /// </summary>
    [Fact]
    public async Task OpenWorkItemsCanBeListed_MostOverdueFirst()
    {
        var tenantId = TestIds.New("wi-list");
        var now = DateTimeOffset.UtcNow;

        var urgent = TestIds.New("op");
        var later = TestIds.New("op");
        var closed = TestIds.New("op");

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await SeedOperationAsync(ctx, tenantId, urgent, OperationStatus.ManualReview);
            await SeedOperationAsync(ctx, tenantId, later, OperationStatus.ManualReview);
            await SeedOperationAsync(ctx, tenantId, closed, OperationStatus.Booked);

            ctx.OperationWorkItems.Add(OpenItem(tenantId, later, "compensation_ambiguous", now.AddHours(3)));
            ctx.OperationWorkItems.Add(OpenItem(tenantId, urgent, "reconcile_no_effect_budget_exhausted", now.AddMinutes(-30)));

            var resolved = OpenItem(tenantId, closed, "sla_escalation", now.AddHours(1));
            resolved.State = "resolved";
            ctx.OperationWorkItems.Add(resolved);

            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var handler = new ListOpenWorkItemsQueryHandler(
            new OperationWorkItemRepository(readCtx),
            new BookingOperationRepository(readCtx));

        var items = await handler.Handle(new ListOpenWorkItemsQuery(tenantId, 50), CancellationToken.None);

        // Only the OPEN ones, and the overdue one first — the order the work should be done in.
        Assert.Equal(2, items.Count);
        Assert.Equal(urgent, items[0].OperationId);
        Assert.Equal(later, items[1].OperationId);
        Assert.DoesNotContain(items, i => i.OperationId == closed);

        // The reason and deadline travel with it — a bare operation id would not tell an operator
        // what to do or whether it is late.
        Assert.Equal("reconcile_no_effect_budget_exhausted", items[0].Reason);
        Assert.True(items[0].SlaDeadline < now);

        // …and the operation's CURRENT status, so an item whose operation has since moved on is
        // recognisable as such rather than sending someone to look at a settled booking.
        Assert.Equal(OperationStatus.ManualReview, items[0].OperationStatus);
    }

    /// <summary>
    /// The listing is tenant-scoped (Inv-13). Work items name operations and reasons, so a leak here
    /// would expose one venue's recovery queue to another.
    /// </summary>
    [Fact]
    public async Task OpenWorkItemsAreScopedToTheTenant()
    {
        var mine = TestIds.New("wi-mine");
        var theirs = TestIds.New("wi-theirs");

        var myOp = TestIds.New("op");
        var theirOp = TestIds.New("op");

        await using (var ctx = fixture.CreateContext(mine))
        {
            await SeedOperationAsync(ctx, mine, myOp, OperationStatus.ManualReview);
            ctx.OperationWorkItems.Add(OpenItem(mine, myOp, "compensation_ambiguous", DateTimeOffset.UtcNow));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = fixture.CreateContext(theirs))
        {
            await SeedOperationAsync(ctx, theirs, theirOp, OperationStatus.ManualReview);
            ctx.OperationWorkItems.Add(OpenItem(theirs, theirOp, "compensation_ambiguous", DateTimeOffset.UtcNow));
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(mine);
        var handler = new ListOpenWorkItemsQueryHandler(
            new OperationWorkItemRepository(readCtx),
            new BookingOperationRepository(readCtx));

        var items = await handler.Handle(new ListOpenWorkItemsQuery(mine, 50), CancellationToken.None);

        Assert.All(items, i => Assert.Equal(myOp, i.OperationId));
        Assert.DoesNotContain(items, i => i.OperationId == theirOp);
    }

    /// <summary>
    /// A second escalation for the same operation is a benign no-op — it must NOT abort the
    /// transaction.
    ///
    /// <para>This is why the escalator moved from read-then-add to
    /// <c>TryAddOpenAsync</c>. The partial unique index always stopped the duplicate ROW; what it
    /// did not stop was the losing INSERT raising 23505, which aborts the whole ambient transaction
    /// — taking the status transition, the outbox row and the audit event down with it. An
    /// escalation would be lost to a race whose correct outcome was "someone else already did it".
    /// </para>
    /// </summary>
    [Fact]
    public async Task ASecondOpenWorkItemIsRefusedWithoutPoisoningTheTransaction()
    {
        var tenantId = TestIds.New("wi-race");
        var operationId = TestIds.New("op");

        await using (var ctx = fixture.CreateContext(tenantId))
            await SeedOperationAsync(ctx, tenantId, operationId, OperationStatus.ManualReview);

        await using var txCtx = fixture.CreateContext(tenantId);
        await using var tx = await txCtx.Database.BeginTransactionAsync();

        var repo = new OperationWorkItemRepository(txCtx);

        var first = await repo.TryAddOpenAsync(
            OpenItem(tenantId, operationId, "compensation_ambiguous", DateTimeOffset.UtcNow));
        var second = await repo.TryAddOpenAsync(
            OpenItem(tenantId, operationId, "sla_escalation", DateTimeOffset.UtcNow));

        Assert.True(first);
        Assert.False(second);

        // The load-bearing assertion: the transaction is still usable. A raised 23505 would have
        // aborted it and this write would fail with "current transaction is aborted".
        await txCtx.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE public.booking_operations SET status_reason = 'still-alive' WHERE tenant_id = {tenantId} AND operation_id = {operationId}");

        await tx.CommitAsync();

        await using var verify = fixture.CreateContext(tenantId);
        var open = await verify.OperationWorkItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(w => w.TenantId == tenantId && w.OperationId == operationId && w.State == "open");

        Assert.Equal(1, open);
    }
}
