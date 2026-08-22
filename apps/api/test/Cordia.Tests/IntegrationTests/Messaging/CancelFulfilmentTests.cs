#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Messaging;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>Provider adapter whose cancel outcome the test controls.</summary>
file sealed class CancelFakeAdapter : IBookingProviderAdapter
{
    public string ProviderName => "fake";

    public ProviderResultKind CancelResult { get; set; } = ProviderResultKind.Created;

    public int CancelCallCount { get; private set; }
    public int CreateCallCount { get; private set; }

    public Task<ProviderResult> CreateBookingAsync(ProviderBookingRequest request, CancellationToken ct = default)
    {
        CreateCallCount++;
        return Task.FromResult(new ProviderResult(ProviderResultKind.Created, "fake_ref_cancel"));
    }

    public Task<ProviderResult> LookupByIdempotencyKeyAsync(string providerAccount, string providerIdempotencyKey, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> LookupByReferenceAsync(string providerAccount, string providerReference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> GetStatusAsync(string providerAccount, string providerReference, CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.NotFound));

    public Task<ProviderResult> ModifyBookingAsync(
        string providerAccount, string providerReference, ProviderBookingRequest request,
        CancellationToken ct = default)
        => Task.FromResult(new ProviderResult(ProviderResultKind.Created, providerReference));

    public Task<ProviderResult> CancelBookingAsync(string providerAccount, string providerReference, CancellationToken ct = default)
    {
        CancelCallCount++;
        return Task.FromResult(new ProviderResult(CancelResult, providerReference));
    }
}

/// <summary>
/// End-to-end fulfilment of a <c>cancel</c> operation (spec 08 §5.3, spec 06 §11).
///
/// Before this path existed, a cancel was driven through the create pipeline: it called
/// <c>CreateBookingAsync</c>, produced a <b>second booking</b>, and left the original stuck in
/// <c>cancellation_pending</c>. These tests drive a real cancel operation through Postgres and
/// assert the booking actually ends up cancelled and the seat is given back.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class CancelFulfilmentTests(PostgresFixture fixture)
{
    /// <param name="tenantId">
    /// The test's tenant. Bounds the publisher's claim phase to this tenant so a tick cannot
    /// dispatch outbox rows written by a test in the parallel AcceptanceApi collection through this
    /// container's fake adapter (issue #90 — see <see cref="OutboxTenantScope"/>).
    /// </param>
    private static ServiceProvider BuildContainer(IBookingProviderAdapter fakeAdapter, string tenantId)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CordiaDBConnection"] = PostgresFixture.TestConnStr,
                ["Messaging:Publisher:Enabled"] = "false",
                ["Messaging:ScheduledJobPoller:Enabled"] = "false",
                ["Messaging:StepSweeper:Enabled"] = "false",
                ["Messaging:NotificationReconcile:Enabled"] = "false",
                ["Messaging:MissingNotificationAudit:Enabled"] = "false",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);
        services.AddSingleton(fakeAdapter);
        services.ScopeOutboxClaimToTenant(tenantId);

        return services.BuildServiceProvider();
    }

    private static OutboxPublisherHostedService BuildPublisher(IServiceProvider sp)
        => new(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<MessagingOptions>>(),
            NullLogger<OutboxPublisherHostedService>.Instance);

    // There used to be a NeutralizeExistingUnpublishedAsync() here that stamped EVERY tenant's
    // unpublished outbox rows as published before each tick, so the cross-tenant claim would only
    // find this test's message. It could not work: it settles the backlog that exists at that
    // instant, and the AcceptanceApi collection — which xUnit runs in parallel against the same
    // database — keeps inserting booking.command.requested.v1 rows afterwards. It also stamped
    // those foreign rows published, corrupting a concurrent test's state. Bounding the claim to
    // this test's tenant (OutboxTenantScope) is what actually isolates the tick, and it makes the
    // sweep unnecessary: every test here mints a fresh GUID tenant with no backlog of its own.

    /// <summary>
    /// Seeds a confirmed booking, the create operation that produced it (with a Finalized hold
    /// occupying slot capacity), and a pending cancel operation ready for the driver.
    /// </summary>
    private async Task<(string CancelOperationId, string BookingId, string SlotId, string MessageId)>
        SeedCancellableBookingAsync(string tenantId)
    {
        var bookingId = $"bk-{Guid.NewGuid():N}";
        var slotId = $"slot-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";

        await using var ctx = fixture.CreateContext(tenantId);

        // The create operation that owns the capacity.
        var originOperationId = await TestData.SeedOperationAsync(ctx, tenantId, OperationStatus.Booked);
        var origin = await ctx.BookingOperations.FirstAsync(o => o.OperationId == originOperationId);
        origin.BookingId = bookingId;
        origin.OperationType = OperationTypeEnum.Create;

        ctx.Bookings.Add(new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = "CORD-CANCEL",
            ProviderReference = "fake_ref_cancel",
            CustomerId = "cust-1",
            LifecycleStatus = BookingLifecycleStatus.CancellationPending,
            Version = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        // A slot with the seat already counted as BOOKED, plus the Finalized hold that put it there.
        ctx.CapacitySlots.Add(new CapacitySlot
        {
            TenantId = tenantId,
            SlotId = slotId,
            ResourceId = "res-1",
            StartsAt = DateTimeOffset.UtcNow.AddDays(1),
            TotalCapacity = 10,
            HeldCapacity = 0,
            BookedCapacity = 2,
            Version = 0,
        });
        ctx.CapacityHolds.Add(new CapacityHold
        {
            TenantId = tenantId,
            HoldId = $"hold-{Guid.NewGuid():N}",
            OperationId = originOperationId,
            WorkflowStep = "reserve",
            SlotId = slotId,
            ReservedCapacity = 2,
            Status = HoldStatus.Finalized,
            ReleaseReason = ReleaseReason.Finalized,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Version = 0,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        // The cancellation — its own operation, targeting the booking.
        var cancelOperationId = await TestData.SeedOperationAsync(ctx, tenantId, OperationStatus.Accepted);
        var cancelOp = await ctx.BookingOperations.FirstAsync(o => o.OperationId == cancelOperationId);
        cancelOp.OperationType = OperationTypeEnum.Cancel;
        cancelOp.BookingId = bookingId;

        ctx.OutboxMessages.Add(new OutboxMessage
        {
            TenantId = tenantId,
            MessageId = messageId,
            MessageType = "booking.command.requested.v1",
            CorrelationId = cancelOperationId,
            OperationId = cancelOperationId,
            Payload = $"{{\"operation_id\":\"{cancelOperationId}\",\"tenant_id\":\"{tenantId}\"}}",
            OccurredAt = DateTimeOffset.UtcNow,
        });

        await ctx.SaveChangesAsync();
        return (cancelOperationId, bookingId, slotId, messageId);
    }

    // ---------------------------------------------------------------------
    // Happy path
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CancelOperation_CancelsTheBooking_FreesCapacity_AndCreatesNoSecondBooking()
    {
        var tenantId = $"cancel-ok-{Guid.NewGuid():N}";
        var (cancelOperationId, bookingId, slotId, _) = await SeedCancellableBookingAsync(tenantId);

        var adapter = new CancelFakeAdapter { CancelResult = ProviderResultKind.Created };
        await using var sp = BuildContainer(adapter, tenantId);
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        // The whole point: cancel, never create.
        Assert.Equal(1, adapter.CancelCallCount);
        Assert.Equal(0, adapter.CreateCallCount);

        await using var ctx = fixture.CreateContext(tenantId);

        // The cancel operation succeeded. `Booked` is the generic success terminal — D1 defines
        // no `cancelled` operation status; the BOOKING carries the cancellation.
        var op = await ctx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == cancelOperationId);
        Assert.Equal(OperationStatus.Booked, op.Status);
        Assert.Equal("cancelled", op.StatusReason);

        var booking = await ctx.Bookings.AsNoTracking()
            .FirstAsync(b => b.TenantId == tenantId && b.BookingId == bookingId);
        Assert.Equal(BookingLifecycleStatus.Cancelled, booking.LifecycleStatus);

        // The seat is genuinely back: booked capacity decremented, hold released.
        var slot = await ctx.CapacitySlots.AsNoTracking()
            .FirstAsync(s => s.TenantId == tenantId && s.SlotId == slotId);
        Assert.Equal(0, slot.BookedCapacity);

        var hold = await ctx.CapacityHolds.AsNoTracking()
            .FirstAsync(h => h.TenantId == tenantId && h.SlotId == slotId);
        Assert.Equal(HoldStatus.Released, hold.Status);

        // No second booking row was created.
        var bookingCount = await ctx.Bookings.AsNoTracking()
            .CountAsync(b => b.TenantId == tenantId);
        Assert.Equal(1, bookingCount);

        var cancelStep = await ctx.WorkflowSteps.AsNoTracking()
            .FirstAsync(s => s.TenantId == tenantId && s.OperationId == cancelOperationId && s.StepType == "cancel");
        Assert.Equal(WorkflowStepStatus.Succeeded, cancelStep.Status);

        // Inv-8: the customer message is committed with the state, not before it.
        var notification = await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId
                                      && m.OperationId == cancelOperationId
                                      && m.MessageType == "notification.cancel_confirmed.v1");
        Assert.NotNull(notification);

        var audit = await ctx.AuditEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId
                                      && e.OperationId == cancelOperationId
                                      && e.EventType == "operation.cancelled");
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task CancelOperation_NeverReservesCapacityOfItsOwn()
    {
        var tenantId = $"cancel-nores-{Guid.NewGuid():N}";
        var (cancelOperationId, _, _, _) = await SeedCancellableBookingAsync(tenantId);

        await using var sp = BuildContainer(new CancelFakeAdapter(), tenantId);
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        await using var ctx = fixture.CreateContext(tenantId);

        // A cancellation gives capacity back; routing it through ReservingCapacity/Held would
        // have it take a seat on its way to releasing one.
        var reserveStep = await ctx.WorkflowSteps.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId
                                      && s.OperationId == cancelOperationId
                                      && s.StepType == "reserve");
        Assert.Null(reserveStep);

        var ownHold = await ctx.CapacityHolds.AsNoTracking()
            .FirstOrDefaultAsync(h => h.TenantId == tenantId && h.OperationId == cancelOperationId);
        Assert.Null(ownHold);
    }

    // ---------------------------------------------------------------------
    // Inv-7 — ambiguity reconciles
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AmbiguousCancel_ReconcilesWithoutTouchingBookingOrCapacity()
    {
        var tenantId = $"cancel-amb-{Guid.NewGuid():N}";
        var (cancelOperationId, bookingId, slotId, _) = await SeedCancellableBookingAsync(tenantId);

        var adapter = new CancelFakeAdapter { CancelResult = ProviderResultKind.Ambiguous };
        await using var sp = BuildContainer(adapter, tenantId);
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        Assert.Equal(1, adapter.CancelCallCount);

        await using var ctx = fixture.CreateContext(tenantId);

        var op = await ctx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == cancelOperationId);
        Assert.Equal(OperationStatus.Reconciling, op.Status);

        // We do not know whether the provider cancelled, so nothing observable may change.
        var booking = await ctx.Bookings.AsNoTracking()
            .FirstAsync(b => b.TenantId == tenantId && b.BookingId == bookingId);
        Assert.Equal(BookingLifecycleStatus.CancellationPending, booking.LifecycleStatus);

        var slot = await ctx.CapacitySlots.AsNoTracking()
            .FirstAsync(s => s.TenantId == tenantId && s.SlotId == slotId);
        Assert.Equal(2, slot.BookedCapacity);

        var notification = await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.TenantId == tenantId
                                      && m.OperationId == cancelOperationId
                                      && m.MessageType == "notification.cancel_confirmed.v1");
        Assert.Null(notification);
    }

    // ---------------------------------------------------------------------
    // Inv-5 — duplicate delivery
    // ---------------------------------------------------------------------

    [Fact]
    public async Task RedeliveredCancel_DoesNotGiveTheSameSeatBackTwice()
    {
        var tenantId = $"cancel-dup-{Guid.NewGuid():N}";
        var (cancelOperationId, bookingId, slotId, _) = await SeedCancellableBookingAsync(tenantId);

        var adapter = new CancelFakeAdapter();
        await using var sp = BuildContainer(adapter, tenantId);
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        // Re-enqueue the same command, as a broker redelivery would.
        await using (var requeue = fixture.CreateContext(tenantId))
        {
            requeue.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = $"msg-{Guid.NewGuid():N}",
                MessageType = "booking.command.requested.v1",
                CorrelationId = cancelOperationId,
                OperationId = cancelOperationId,
                Payload = $"{{\"operation_id\":\"{cancelOperationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await requeue.SaveChangesAsync();
        }

        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        await using var ctx = fixture.CreateContext(tenantId);

        // The seat comes back exactly once, however many times the message arrives.
        var slot = await ctx.CapacitySlots.AsNoTracking()
            .FirstAsync(s => s.TenantId == tenantId && s.SlotId == slotId);
        Assert.Equal(0, slot.BookedCapacity);

        var booking = await ctx.Bookings.AsNoTracking()
            .FirstAsync(b => b.TenantId == tenantId && b.BookingId == bookingId);
        Assert.Equal(BookingLifecycleStatus.Cancelled, booking.LifecycleStatus);

        var notifications = await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(m => m.TenantId == tenantId
                             && m.OperationId == cancelOperationId
                             && m.MessageType == "notification.cancel_confirmed.v1");
        Assert.Equal(1, notifications);
    }

    // ---------------------------------------------------------------------
    // Regression: a create must still take the capacity path
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CreateOperation_StillRunsThroughTheCapacityPath()
    {
        var tenantId = $"cancel-regress-{Guid.NewGuid():N}";
        var messageId = $"msg-{Guid.NewGuid():N}";
        string operationId;

        await using (var seed = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seed, tenantId, OperationStatus.Accepted);
            seed.OutboxMessages.Add(new OutboxMessage
            {
                TenantId = tenantId,
                MessageId = messageId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = operationId,
                OperationId = operationId,
                Payload = $"{{\"operation_id\":\"{operationId}\",\"tenant_id\":\"{tenantId}\"}}",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var adapter = new CancelFakeAdapter();
        await using var sp = BuildContainer(adapter, tenantId);
        await BuildPublisher(sp).PublishPendingBatchAsync(CancellationToken.None);

        // A create books; it must never take the cancel branch.
        Assert.Equal(0, adapter.CancelCallCount);

        await using var ctx = fixture.CreateContext(tenantId);
        var op = await ctx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(OperationStatus.Booked, op.Status);
    }
}
