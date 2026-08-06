#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Application.Handlers.Waitlist;
using Cordia.Application.Handlers.Waitlist.AutoAccept;
using Cordia.Application.Handlers.Waitlist.Cancel;
using Cordia.Application.Handlers.Waitlist.Decline;
using Cordia.Application.Handlers.Waitlist.Enroll;
using Cordia.Application.Handlers.Waitlist.Expire;
using Cordia.Application.Handlers.Waitlist.Match;
using Cordia.Application.Handlers.Waitlist.ReconcileAccept;
using Cordia.Application.Common.Audit;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Waitlist;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cordia.Tests.IntegrationTests.Waitlist;

/// <summary>
/// Integration coverage for the spec-11 §5.1 waitlist behavior layer against real PostgreSQL:
/// enroll/cancel, opening-triggered match (offer / auto-book / no-capacity), and the offer
/// lifecycle (accept / decline / expire), including idempotency and the re-match-after-release
/// loop. Each test uses a unique tenant and asserts on its own ids only (shared test DB).
///
/// <para>The HTTP-driven handlers (Enroll/Cancel/Accept/Decline) open their own transaction via
/// <see cref="UnitOfWork"/> and are invoked directly. The queue-driven handlers (Match/Expire)
/// implement <c>IInboxConsumerRequest</c> and rely on an ambient inbox transaction in production,
/// so the tests wrap them in an explicit transaction to mirror that boundary.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class WaitlistFlowTests(PostgresFixture fixture)
{
    // ── Handler builders (real repositories over a shared per-tenant context) ──────────────────

    private static MatchWaitlistOfferCommandHandler BuildMatch(ApplicationDbContext ctx)
        => new(
            new WaitlistEnrollmentRepository(ctx),
            new WaitlistOfferRepository(ctx),
            new FirstPartyCapacityReservation(ctx, new CapacityHoldRepository(ctx), new Cordia.Infrastructure.Observability.Metrics()),
            new OfferExpiryScheduler(new ScheduledJobRepository(ctx)),
            new OutboxRepository(ctx),
            new AuditEventRepository(ctx),
            NullLogger<MatchWaitlistOfferCommandHandler>.Instance);

    // Spec 11 §5.1 (divergence #1): accept now routes through the spec 04 → 06 front door via IMediator
    // rather than finalizing its own hold. Driving that end-to-end from a hand-built handler needs a real
    // mediator wired to the 04 handler + capacity/06 stack — the Phase-6 integration rewrite. The only
    // remaining caller of this builder is the (skipped) obsolete accept test; the null! mediator is never
    // dereferenced until that test is re-authored.
    private static DeclineWaitlistOfferCommandHandler BuildDecline(ApplicationDbContext ctx)
        => new(
            new WaitlistOfferRepository(ctx),
            new WaitlistEnrollmentRepository(ctx),
            new FirstPartyCapacityReservation(ctx, new CapacityHoldRepository(ctx), new Cordia.Infrastructure.Observability.Metrics()),
            new OutboxRepository(ctx),
            new AuditEventRepository(ctx),
            new AuditWriter(new AuditEventRepository(ctx)),
            new UnitOfWork(ctx));

    private static ExpireWaitlistOfferCommandHandler BuildExpire(ApplicationDbContext ctx)
        => new(
            new WaitlistOfferRepository(ctx),
            new WaitlistEnrollmentRepository(ctx),
            new FirstPartyCapacityReservation(ctx, new CapacityHoldRepository(ctx), new Cordia.Infrastructure.Observability.Metrics()),
            new OutboxRepository(ctx),
            new AuditEventRepository(ctx),
            NullLogger<ExpireWaitlistOfferCommandHandler>.Instance);

    private static EnrollWaitlistCommandHandler BuildEnroll(ApplicationDbContext ctx)
        => new(
            new WaitlistEnrollmentRepository(ctx),
            new WaitlistOfferRepository(ctx),
            new BookingOperationRepository(ctx),
            new AuditEventRepository(ctx),
            new IdempotencyRepository(ctx),
            new CanonicalRequestHasher(),
            new UnitOfWork(ctx));

    private static CancelWaitlistEnrollmentCommandHandler BuildCancel(ApplicationDbContext ctx)
        => new(
            new WaitlistEnrollmentRepository(ctx),
            new WaitlistOfferRepository(ctx),
            new FirstPartyCapacityReservation(ctx, new CapacityHoldRepository(ctx), new Cordia.Infrastructure.Observability.Metrics()),
            new OutboxRepository(ctx),
            new AuditEventRepository(ctx),
            new AuditWriter(new AuditEventRepository(ctx)),
            new UnitOfWork(ctx));


    /// <summary>
    /// Seeds a booking operation at <paramref name="status"/>, with the intent + revision its
    /// foreign key requires. Used to stand in for the booking a waitlist accept minted.
    /// </summary>
    private static void SeedOperation(
        ApplicationDbContext ctx, string tenantId, string operationId, OperationStatus status)
    {
        var intentId = TestIds.New("int");
        var now = DateTimeOffset.UtcNow;

        ctx.BookingIntents.Add(new BookingIntent
        {
            TenantId = tenantId,
            IntentId = intentId,
            OperationType = Cordia.Domain.Generated.OperationTypeEnum.Create,
            CreatedAt = now,
        });
        ctx.BookingIntentRevisions.Add(new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = "{}",
            PayloadHash = TestIds.New("h"),
            SchemaVersion = "v1",
            TenantPolicyVersion = "v1",
            CreatedAt = now,
        });
        ctx.BookingOperations.Add(new BookingOperation
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = Cordia.Domain.Generated.OperationTypeEnum.Create,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    // ── Seeding helpers ────────────────────────────────────────────────────────────────────────

    private async Task<string> SeedSlotAsync(
        string tenantId, DateTimeOffset startsAt, int total, int held)
    {
        var resourceId = TestIds.New("res");
        var slotId = $"{resourceId}|{startsAt:O}";
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.CapacitySlots.Add(new CapacitySlot
        {
            TenantId = tenantId,
            SlotId = slotId,
            ResourceId = resourceId,
            StartsAt = startsAt,
            TotalCapacity = total,
            HeldCapacity = held,
            BookedCapacity = 0,
            Version = 0,
        });
        await ctx.SaveChangesAsync();
        return slotId;
    }

    private async Task<string> SeedEnrollmentAsync(
        string tenantId,
        DateTimeOffset slotStart,
        int partySize = 2,
        AutoBookGrant? grant = null,
        DateTimeOffset? createdAt = null)
    {
        var enrollmentId = TestIds.New("wle_");
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.WaitlistEnrollments.Add(new WaitlistEnrollment
        {
            TenantId = tenantId,
            EnrollmentId = enrollmentId,
            CustomerRef = "cust-1",
            AcceptableFrom = slotStart.AddHours(-2),
            AcceptableTo = slotStart.AddHours(2),
            PartySize = partySize,
            LocationId = "loc-1",
            ServiceId = "svc-1",
            OfferLifetimeSeconds = 900,
            NotificationChannel = "email",
            AutoBook = grant?.ToJson(),
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return enrollmentId;
    }

    /// <summary>Seeds an active hold + an Offered offer over an existing slot. Returns (offerId, holdId, operationId).</summary>
    private async Task<(string offerId, string holdId, string operationId)> SeedOfferedAsync(
        string tenantId, string enrollmentId, string slotId, DateTimeOffset slotStart, int seats = 2, int reserved = 2)
    {
        var offerId = TestIds.New("wlo_");
        var holdId = TestIds.New("hold_");
        var operationId = TestIds.New("op_wl_");
        var now = DateTimeOffset.UtcNow;

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.CapacityHolds.Add(new CapacityHold
        {
            TenantId = tenantId,
            HoldId = holdId,
            OperationId = operationId,
            WorkflowStep = "waitlist",
            SlotId = slotId,
            ReservedCapacity = reserved,
            Status = HoldStatus.Active,
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(15),
            Version = 0,
        });
        ctx.WaitlistOffers.Add(new WaitlistOffer
        {
            TenantId = tenantId,
            OfferId = offerId,
            EnrollmentId = enrollmentId,
            HoldId = holdId,
            OperationId = operationId,
            SlotId = slotId,
            SlotStartAt = slotStart,
            AvailableSeats = seats,
            Status = WaitlistOfferStatus.Offered,
            MatchedAt = now,
            OfferedAt = now,
            OfferExpiresAt = now.AddMinutes(15),
            CreatedAt = now,
            Version = 0,
        });
        await ctx.SaveChangesAsync();
        return (offerId, holdId, operationId);
    }

    private async Task RunMatchAsync(string tenantId, MatchWaitlistOfferCommand cmd)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = BuildMatch(ctx);
        await using var tx = await ctx.Database.BeginTransactionAsync();
        await handler.Handle(cmd, CancellationToken.None);
        await tx.CommitAsync();
    }

    private static MatchWaitlistOfferCommand MatchCmd(string tenantId, string slotId, DateTimeOffset slotStart, int seats)
        => new(tenantId, TestIds.New("msg"), slotId, "loc-1", "svc-1", slotId, slotStart, seats);

    // ── Enroll / Cancel ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enroll_PersistsPendingEnrollment_AndAudits()
    {
        var tenantId = TestIds.New("wl-enroll");
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);

        string enrollmentId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            enrollmentId = await BuildEnroll(ctx).Handle(
                new EnrollWaitlistCommand(
                    tenantId, "customer", "cust-1", slotStart.AddHours(-1), slotStart.AddHours(1),
                    2, "loc-1", "svc-1", 900, Deposit: null, "email", AutoBook: null),
                CancellationToken.None);
        }

        await using var assertCtx = fixture.CreateContext(tenantId);
        var e = await assertCtx.WaitlistEnrollments.AsNoTracking()
            .FirstAsync(x => x.EnrollmentId == enrollmentId);
        Assert.Null(e.CancelledAt);
        Assert.Equal(2, e.PartySize);

        var audit = await assertCtx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.TenantId == tenantId && a.SubjectRef == enrollmentId && a.EventType == "waitlist.enrolled");
        Assert.NotNull(audit);
    }

    [Fact]
    public async Task Cancel_StampsCancelled_AndIsIdempotent_AndUnknown404()
    {
        var tenantId = TestIds.New("wl-cancel");
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart);

        await using (var ctx = fixture.CreateContext(tenantId))
            await BuildCancel(ctx).Handle(new CancelWaitlistEnrollmentCommand(tenantId, enrollmentId, "customer", "operator", "waitlist test"), CancellationToken.None);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var e = await ctx.WaitlistEnrollments.AsNoTracking().FirstAsync(x => x.EnrollmentId == enrollmentId);
            Assert.NotNull(e.CancelledAt);
        }

        // Idempotent re-cancel: no throw.
        await using (var ctx = fixture.CreateContext(tenantId))
            await BuildCancel(ctx).Handle(new CancelWaitlistEnrollmentCommand(tenantId, enrollmentId, "customer", "operator", "waitlist test"), CancellationToken.None);

        // Unknown enrollment → 404.
        await using (var ctx = fixture.CreateContext(tenantId))
            await Assert.ThrowsAsync<WaitlistEnrollmentNotFoundException>(() =>
                BuildCancel(ctx).Handle(new CancelWaitlistEnrollmentCommand(tenantId, "wle_missing", "customer", "operator", "waitlist test"), CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_WithLiveOffer_ReleasesHold_AndEnqueuesRematch()
    {
        // Review 2026-07-03 C10: cancelling an enrollment that holds a live offer must re-match
        // the freed opening to the next eligible enrollee, exactly like decline/expire (§5.1/FM#1)
        // — otherwise the slot silently leaves the waitlist queue.
        var tenantId = TestIds.New("wl-cancel-live");
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 4, held: 2);
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2);
        var (offerId, holdId, _) = await SeedOfferedAsync(tenantId, enrollmentId, slotId, slotStart, seats: 4, reserved: 2);

        await using (var ctx = fixture.CreateContext(tenantId))
            await BuildCancel(ctx).Handle(new CancelWaitlistEnrollmentCommand(tenantId, enrollmentId, "customer", "operator", "waitlist test"), CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        var offer = await assertCtx.WaitlistOffers.AsNoTracking().IgnoreQueryFilters().SingleAsync(o => o.OfferId == offerId);
        Assert.Equal(WaitlistOfferStatus.Released, offer.Status);

        var hold = await assertCtx.CapacityHolds.AsNoTracking().IgnoreQueryFilters().SingleAsync(h => h.HoldId == holdId);
        Assert.Equal(HoldStatus.Released, hold.Status);

        // Re-match enqueued for the freed opening (same recovery as decline/expire).
        Assert.True(await assertCtx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.MessageType == WaitlistRematchMessage.MessageType));
    }

    // ── Match ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Match_ReservesHoldAndOffers_NextEligibleEnrollee()
    {
        var tenantId = TestIds.New("wl-match");
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 4, held: 0);
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2);

        await RunMatchAsync(tenantId, MatchCmd(tenantId, slotId, slotStart, seats: 4));

        await using var ctx = fixture.CreateContext(tenantId);

        var offer = await ctx.WaitlistOffers.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(o => o.TenantId == tenantId && o.EnrollmentId == enrollmentId);
        Assert.Equal(WaitlistOfferStatus.Offered, offer.Status);
        Assert.NotNull(offer.HoldId);
        Assert.NotNull(offer.OfferExpiresAt);

        // Temporary hold incremented the slot held capacity by the party size.
        var slot = await ctx.CapacitySlots.AsNoTracking().IgnoreQueryFilters().SingleAsync(s => s.SlotId == slotId);
        Assert.Equal(2, slot.HeldCapacity);

        // Expiry job scheduled + customer offer notification queued + audit.
        Assert.True(await ctx.ScheduledJobs.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(j => j.TenantId == tenantId && j.JobId == $"sj_offerexp_{offer.OfferId}"));
        Assert.True(await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.MessageType == MatchWaitlistOfferCommandHandler.OfferNotifiedEventType));
        Assert.True(await ctx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(a => a.TenantId == tenantId && a.EventType == "waitlist.offered"));
    }

    [Fact]
    public async Task Match_AutoBooks_UnderSatisfiedConsentGrant()
    {
        var tenantId = TestIds.New("wl-autobook");
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 4, held: 0);
        var grant = new AutoBookGrant(
            ConsentEvidenceId: "consent-1",
            ConsentedAt: DateTimeOffset.UtcNow,
            MaxPartySize: 4,
            ConstraintFrom: slotStart.AddHours(-3),
            ConstraintTo: slotStart.AddHours(3),
            MaxDepositAmountMinor: 0,
            DepositCurrency: "USD");
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2, grant: grant);

        await RunMatchAsync(tenantId, MatchCmd(tenantId, slotId, slotStart, seats: 4));

        await using var ctx = fixture.CreateContext(tenantId);

        var offer = await ctx.WaitlistOffers.AsNoTracking().IgnoreQueryFilters()
            .FirstAsync(o => o.TenantId == tenantId && o.EnrollmentId == enrollmentId);
        // §20/Inv-1: auto-book now routes through spec-04, so the match leaves the offer Offered and
        // enqueues the auto-accept step (which mints the confirmed revision + claims it); the match no
        // longer finalizes capacity or emits a bespoke booking event here.
        Assert.Equal(WaitlistOfferStatus.Offered, offer.Status);

        // Hold NOT finalized by the match — the 06 engine finalizes it after the spec-04 submit. The
        // slot capacity is still HELD (not booked) at this point.
        var slot = await ctx.CapacitySlots.AsNoTracking().IgnoreQueryFilters().SingleAsync(s => s.SlotId == slotId);
        Assert.Equal(2, slot.HeldCapacity);
        Assert.Equal(0, slot.BookedCapacity);

        // Auto-accept step enqueued (routes through the spec-04 front door); intent audited.
        Assert.True(await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.MessageType == AutoAcceptWaitlistOfferCommand.MessageType));
        Assert.True(await ctx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(a => a.TenantId == tenantId && a.EventType == "waitlist.auto_book_scheduled"));
        // Offer-expiry job IS scheduled as the backstop: if the auto-accept step never books (grant no
        // longer permits, or the message is undeliverable) the expiry releases the hold + re-matches, so
        // the hold and offer never leak. If auto-accept books first, the expiry job no-ops.
        Assert.True(await ctx.ScheduledJobs.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(j => j.TenantId == tenantId && j.JobId == $"sj_offerexp_{offer.OfferId}"));
    }

    [Fact]
    public async Task Match_NoCapacity_SettlesWithoutOffer()
    {
        var tenantId = TestIds.New("wl-nocap");
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 2, held: 2); // full
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2);

        await RunMatchAsync(tenantId, MatchCmd(tenantId, slotId, slotStart, seats: 2));

        await using var ctx = fixture.CreateContext(tenantId);
        Assert.False(await ctx.WaitlistOffers.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(o => o.TenantId == tenantId && o.EnrollmentId == enrollmentId));
    }

    // ── Accept ───────────────────────────────────────────────────────────────────────────────

    // Accept is covered by WaitlistAcceptFlowTests: it now routes through spec 04 and needs a
    // real IMediator, so it is driven through the HTTP door against the Alba host rather than a
    // hand-built handler (this class has no host).

    // ── Decline ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Decline_ReleasesHold_AndEnqueuesRematch()
    {
        var tenantId = TestIds.New("wl-decline");
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 4, held: 2);
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2);
        var (offerId, holdId, _) = await SeedOfferedAsync(tenantId, enrollmentId, slotId, slotStart, seats: 4, reserved: 2);

        await using (var ctx = fixture.CreateContext(tenantId))
            await BuildDecline(ctx).Handle(new DeclineWaitlistOfferCommand(tenantId, offerId, "customer", "operator", "waitlist test"), CancellationToken.None);

        await using var assertCtx = fixture.CreateContext(tenantId);

        var offer = await assertCtx.WaitlistOffers.AsNoTracking().IgnoreQueryFilters().SingleAsync(o => o.OfferId == offerId);
        Assert.Equal(WaitlistOfferStatus.Declined, offer.Status);

        var hold = await assertCtx.CapacityHolds.AsNoTracking().IgnoreQueryFilters().SingleAsync(h => h.HoldId == holdId);
        Assert.Equal(HoldStatus.Released, hold.Status);

        // Held capacity rolled back so the slot is free again.
        var slot = await assertCtx.CapacitySlots.AsNoTracking().IgnoreQueryFilters().SingleAsync(s => s.SlotId == slotId);
        Assert.Equal(0, slot.HeldCapacity);

        // Re-match enqueued for the freed opening.
        Assert.True(await assertCtx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.MessageType == WaitlistRematchMessage.MessageType));
        Assert.True(await assertCtx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(a => a.TenantId == tenantId && a.EventType == "waitlist.declined"));
    }

    // ── Expire ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expire_ExpiresOffer_ReleasesHold_AndRematches()
    {
        var tenantId = TestIds.New("wl-expire");
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 4, held: 2);
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2);
        var (offerId, holdId, _) = await SeedOfferedAsync(tenantId, enrollmentId, slotId, slotStart, seats: 4, reserved: 2);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var handler = BuildExpire(ctx);
            await using var tx = await ctx.Database.BeginTransactionAsync();
            await handler.Handle(new ExpireWaitlistOfferCommand(tenantId, TestIds.New("msg"), null, offerId), CancellationToken.None);
            await tx.CommitAsync();
        }

        await using var assertCtx = fixture.CreateContext(tenantId);

        var offer = await assertCtx.WaitlistOffers.AsNoTracking().IgnoreQueryFilters().SingleAsync(o => o.OfferId == offerId);
        Assert.Equal(WaitlistOfferStatus.Expired, offer.Status);

        var hold = await assertCtx.CapacityHolds.AsNoTracking().IgnoreQueryFilters().SingleAsync(h => h.HoldId == holdId);
        Assert.Equal(HoldStatus.Expired, hold.Status);

        var slot = await assertCtx.CapacitySlots.AsNoTracking().IgnoreQueryFilters().SingleAsync(s => s.SlotId == slotId);
        Assert.Equal(0, slot.HeldCapacity);

        Assert.True(await assertCtx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.MessageType == ExpireWaitlistOfferCommandHandler.OfferExpiredEventType));
        Assert.True(await assertCtx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(m => m.TenantId == tenantId && m.MessageType == WaitlistRematchMessage.MessageType));
    }

    [Fact]
    public async Task Expire_OnAlreadyResolvedOffer_SettlesNoop()
    {
        var tenantId = TestIds.New("wl-expire-noop");
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 4, held: 2);
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2);
        var (offerId, _, _) = await SeedOfferedAsync(tenantId, enrollmentId, slotId, slotStart, seats: 4, reserved: 2);

        // Resolve the offer terminal (Accepted) before expiry fires. Set directly rather than through
        // the accept handler so this expiry no-op assertion doesn't depend on the 04→06 accept wiring
        // (Phase 6); this test only cares that a late expiry timer leaves a resolved offer untouched.
        await using (var ctx = fixture.CreateContext(tenantId))
            await new WaitlistOfferRepository(ctx).TryResolveAsync(
                tenantId, offerId, expectedVersion: 0,
                WaitlistOfferStatus.Offered, WaitlistOfferStatus.Accepted, DateTimeOffset.UtcNow,
                CancellationToken.None);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var handler = BuildExpire(ctx);
            await using var tx = await ctx.Database.BeginTransactionAsync();
            await handler.Handle(new ExpireWaitlistOfferCommand(tenantId, TestIds.New("msg"), null, offerId), CancellationToken.None);
            await tx.CommitAsync();
        }

        await using var assertCtx = fixture.CreateContext(tenantId);
        var offer = await assertCtx.WaitlistOffers.AsNoTracking().IgnoreQueryFilters().SingleAsync(o => o.OfferId == offerId);
        Assert.Equal(WaitlistOfferStatus.Accepted, offer.Status); // unchanged — expiry was a no-op
    }

    // -------------------------------------------------------------------------
    // Duplicate protection (docs §12 gap 6).
    //
    // Enrolment had none. An ambiguous network failure plus an operator retry produced a second
    // wle_* row, so the guest sat on the waitlist twice — two offers, two holds against the same
    // capacity, and no error anywhere to notice it by.
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task EnrollingTwiceWithTheSameDetailsYieldsOneEnrollment()
    {
        var tenantId = TestIds.New("wl-idem");
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = BuildEnroll(ctx);
        var command = EnrollCommandFor(tenantId);

        var first = await handler.Handle(command, TestContext.Current.CancellationToken);
        // The retry an operator makes after an ambiguous failure — same details, no key.
        var second = await handler.Handle(command, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
        var rows = ctx.WaitlistEnrollments.Where(e => e.TenantId == tenantId).ToList();
        Assert.Single(rows);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ADeliberatelyDifferentEnrollmentIsStillAllowed()
    {
        // Only an IDENTICAL repeat is deduplicated. A guest asking for a different party size is
        // a second, real enrolment and must not be swallowed.
        var tenantId = TestIds.New("wl-idem2");
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = BuildEnroll(ctx);

        var first = await handler.Handle(EnrollCommandFor(tenantId), TestContext.Current.CancellationToken);
        var second = await handler.Handle(
            EnrollCommandFor(tenantId) with { PartySize = 6 }, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        Assert.Equal(2, ctx.WaitlistEnrollments.Count(e => e.TenantId == tenantId));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReusingAKeyForDifferentDetailsIsAConflict()
    {
        // Inv-3: one key must never be bound to two different requests. Silently returning the
        // first enrolment would tell the operator their second, different request succeeded.
        var tenantId = TestIds.New("wl-idem3");
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = BuildEnroll(ctx);

        await handler.Handle(
            EnrollCommandFor(tenantId) with { IdempotencyKey = "op-key-1" },
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IdempotencyConflictException>(() => handler.Handle(
            EnrollCommandFor(tenantId) with { IdempotencyKey = "op-key-1", PartySize = 9 },
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [Trait("Category", "Integration")]
    [InlineData("channel")]
    [InlineData("lifetime")]
    [InlineData("autobook")]
    public async Task ReusingAKeyForADifferentValueOfANYMaterialFieldIsAConflict(string field)
    {
        // The hash used to cover six of the twelve material fields, so a same-key request that
        // differed only in an UNhashed one matched, was answered as a replay, and silently
        // discarded the change. `notification_channel` decides where the offer is delivered and
        // `offer_lifetime_seconds` when it expires — neither is cosmetic, and both are editable
        // in the console's own drawer, so "correct it and retry" hit exactly this path.
        //
        // The pre-existing conflict test only ever varied PartySize, which was inside the hash,
        // so it passed throughout and proved nothing about the other six.
        var tenantId = TestIds.New($"wl-hash-{field}");
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = BuildEnroll(ctx);
        var first = EnrollCommandFor(tenantId) with { IdempotencyKey = "op-key-hash" };

        await handler.Handle(first, TestContext.Current.CancellationToken);

        var second = field switch
        {
            "channel" => first with { NotificationChannel = "email" },
            "lifetime" => first with { OfferLifetimeSeconds = 1800 },
            _ => first with
            {
                AutoBook = new AutoBookGrant(
                    "consent-1", DateTimeOffset.UtcNow, 4,
                    first.AcceptableFrom, first.AcceptableTo, 0, "AUD"),
            },
        };

        await Assert.ThrowsAsync<IdempotencyConflictException>(
            () => handler.Handle(second, TestContext.Current.CancellationToken));

        // And nothing was written for the rejected request.
        Assert.Single(ctx.WaitlistEnrollments.Where(e => e.TenantId == tenantId));
    }

    // -------------------------------------------------------------------------
    // Replaying a key whose enrolment was CANCELLED (Inv-16).
    //
    // The derived key is a hash of the guest's own details, so the console's own
    // remove-then-re-add flow collides with the enrolment it just reversed — in SECONDS, well
    // inside any dedup window. Answering that replay with the cancelled id reported "added to
    // the waitlist" for a guest sitting on no list, and nothing anywhere raised an error.
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReEnrollingAfterRemovalIsANewEnrollmentNotACancelledReplay()
    {
        var tenantId = TestIds.New("wl-idem4");
        await using var ctx = fixture.CreateContext(tenantId);
        var enroll = BuildEnroll(ctx);
        var command = EnrollCommandFor(tenantId);

        var first = await enroll.Handle(command, TestContext.Current.CancellationToken);
        await BuildCancel(ctx).Handle(
            new CancelWaitlistEnrollmentCommand(tenantId, first, "operator@venue", "operator", "waitlist test"),
            TestContext.Current.CancellationToken);

        // Same details, no key — exactly what the console sends when an operator re-adds a guest.
        var second = await enroll.Handle(command, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);

        var live = ctx.WaitlistEnrollments
            .Where(e => e.TenantId == tenantId && e.CancelledAt == null)
            .ToList();
        Assert.Single(live);
        Assert.Equal(second, live[0].EnrollmentId);
    }

    /// <summary>
    /// A retry of an enrolment whose guest is now BOOKED must not put them back on the waitlist.
    ///
    /// <para>Fulfilment and withdrawal are recorded the same way — both stamp
    /// <c>CancelledAt</c>, because the accept-reconcile path uses it to make a booked enrollee
    /// ineligible for another offer. Treating that as a spent fence re-elected a fresh
    /// <c>wle_*</c> row, so an identical retry left a guest who already has a table waiting for a
    /// second one: eligible for another offer, or under an AutoBookGrant a silent second booking.
    /// The accepted offer is the evidence that tells the two apart — a withdrawal RELEASES its live
    /// offer, it never accepts one.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task RetryingAnEnrolmentThatWasFulfilledReturnsItRatherThanWaitlistingTheGuestAgain()
    {
        var tenantId = TestIds.New("wl-fulfilled");
        await using var ctx = fixture.CreateContext(tenantId);
        var enroll = BuildEnroll(ctx);
        var command = EnrollCommandFor(tenantId);

        var first = await enroll.Handle(command, TestContext.Current.CancellationToken);

        // The enrollee accepted an offer and was booked: the offer is Accepted, the operation it
        // minted reached Booked, and the reconcile path has marked the enrollment terminal so it is
        // never re-offered. The BOOKED operation is the load-bearing part — an accepted offer alone
        // is not fulfilment, because an accept whose booking later failed leaves the enrollee
        // correctly eligible.
        var bookedOperationId = TestIds.New("op_wl");
        ctx.WaitlistOffers.Add(new WaitlistOffer
        {
            TenantId = tenantId,
            OfferId = TestIds.New("wlo"),
            EnrollmentId = first,
            OperationId = bookedOperationId,
            Status = WaitlistOfferStatus.Accepted,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        SeedOperation(ctx, tenantId, bookedOperationId, OperationStatus.Booked);
        var enrollment = ctx.WaitlistEnrollments.Single(e => e.EnrollmentId == first);
        enrollment.CancelledAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        var second = await enroll.Handle(command, TestContext.Current.CancellationToken);

        // The recorded enrolment comes back — no second row, so no second offer and no second book.
        Assert.Equal(first, second);
        Assert.Single(ctx.WaitlistEnrollments.Where(e => e.TenantId == tenantId).ToList());
    }

    /// <summary>
    /// The converse, and the reason the fulfilment test above needs a BOOKED operation: an offer
    /// that was accepted but whose booking then failed is NOT a fulfilment.
    ///
    /// <para>An offer is claimed <c>Accepted</c> before the booking is confirmed and has no
    /// transition out again, so it lingers accepted after a terminal failure — where
    /// <c>ReconcileWaitlistAcceptCommandHandler</c> deliberately leaves the enrollee eligible. If
    /// the guest then withdraws and re-enrols, treating the stale accepted offer as fulfilment
    /// would hand back the CANCELLED id and report "added to the waitlist" for a guest on no list.
    /// </para>
    /// </summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task AnAcceptedOfferWhoseBookingFailedDoesNotCountAsFulfilment()
    {
        var tenantId = TestIds.New("wl-acc-failed");
        await using var ctx = fixture.CreateContext(tenantId);
        var enroll = BuildEnroll(ctx);
        var command = EnrollCommandFor(tenantId) with { IdempotencyKey = "op-key-acc-failed" };

        var first = await enroll.Handle(command, TestContext.Current.CancellationToken);

        var failedOperationId = TestIds.New("op_wl");
        ctx.WaitlistOffers.Add(new WaitlistOffer
        {
            TenantId = tenantId,
            OfferId = TestIds.New("wlo"),
            EnrollmentId = first,
            OperationId = failedOperationId,
            Status = WaitlistOfferStatus.Accepted,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        SeedOperation(ctx, tenantId, failedOperationId, OperationStatus.FailedFinal);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        // The guest gives up and withdraws, then changes their mind and re-enrols identically.
        await BuildCancel(ctx).Handle(
            new CancelWaitlistEnrollmentCommand(tenantId, first, "operator@venue", "operator", "waitlist test"),
            TestContext.Current.CancellationToken);

        var second = await enroll.Handle(command, TestContext.Current.CancellationToken);

        // A genuine withdrawal, so the fence is voided and they really are back on the list.
        Assert.NotEqual(first, second);
        var live = ctx.WaitlistEnrollments
            .Where(e => e.TenantId == tenantId && e.CancelledAt == null)
            .ToList();
        Assert.Single(live);
        Assert.Equal(second, live[0].EnrollmentId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReEnrollingAfterRemovalWithAnExplicitKeyIsAlsoANewEnrollment()
    {
        // An explicit key gets the 365-day window, so without the liveness check this case was
        // the worst of the two — a cancelled enrolment could be replayed back for a year.
        var tenantId = TestIds.New("wl-idem5");
        await using var ctx = fixture.CreateContext(tenantId);
        var enroll = BuildEnroll(ctx);
        var command = EnrollCommandFor(tenantId) with { IdempotencyKey = "op-key-reused" };

        var first = await enroll.Handle(command, TestContext.Current.CancellationToken);
        await BuildCancel(ctx).Handle(
            new CancelWaitlistEnrollmentCommand(tenantId, first, "operator@venue", "operator", "waitlist test"),
            TestContext.Current.CancellationToken);

        var second = await enroll.Handle(command, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        Assert.Single(ctx.WaitlistEnrollments.Where(
            e => e.TenantId == tenantId && e.CancelledAt == null));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ADerivedKeyPastItsWindowLetsAGenuinelySecondEnrolmentThrough()
    {
        // The window says "after an hour, identical details are a NEW intent, not a retry". But
        // the expired record still holds unique(tenant_id, idempotency_key), so simply ignoring
        // it made the creator election lose on conflict and hand back the FIRST enrolment — the
        // window had no effect at all until the retention sweep happened to prune the row, which
        // is a background job and cannot be what makes the next request correct.
        var tenantId = TestIds.New("wl-idem7");
        await using var ctx = fixture.CreateContext(tenantId);
        var enroll = BuildEnroll(ctx);
        var command = EnrollCommandFor(tenantId);

        var first = await enroll.Handle(command, TestContext.Current.CancellationToken);

        // Age the fence past the derived-key window rather than waiting an hour for it.
        var record = ctx.IdempotencyRecords.Single(r => r.TenantId == tenantId);
        record.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        ctx.ChangeTracker.Clear();

        var second = await enroll.Handle(command, TestContext.Current.CancellationToken);

        Assert.NotEqual(first, second);
        // Both are live: neither was cancelled, so this is two real enrolments, not a replay.
        Assert.Equal(2, ctx.WaitlistEnrollments.Count(e => e.TenantId == tenantId && e.CancelledAt == null));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReplayingALiveEnrollmentStillReturnsTheRecordedId()
    {
        // The other half of the same rule, pinned separately: the liveness check must NOT relax
        // spec 04's disposition for an operation whose product still exists. A retry of a LIVE
        // enrolment is still answered with the enrolment already minted, and writes nothing.
        var tenantId = TestIds.New("wl-idem6");
        await using var ctx = fixture.CreateContext(tenantId);
        var enroll = BuildEnroll(ctx);
        var command = EnrollCommandFor(tenantId);

        var first = await enroll.Handle(command, TestContext.Current.CancellationToken);
        var replay = await enroll.Handle(command, TestContext.Current.CancellationToken);

        Assert.Equal(first, replay);
        Assert.Single(ctx.WaitlistEnrollments.Where(e => e.TenantId == tenantId));
    }

    private static EnrollWaitlistCommand EnrollCommandFor(string tenantId)
        => new(
            TenantId: tenantId,
            Actor: "operator@venue",
            CustomerRef: "+61400111222",
            AcceptableFrom: new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero),
            AcceptableTo: new DateTimeOffset(2026, 9, 1, 17, 0, 0, TimeSpan.Zero),
            PartySize: 2,
            LocationId: "loc-1",
            ServiceId: "svc-1",
            OfferLifetimeSeconds: 900,
            Deposit: null,
            NotificationChannel: "sms",
            AutoBook: null);

    // -------------------------------------------------------------------------
    // Locations in use (docs §12 gap 7).
    // -------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ListsTheLocationsAVenueActuallyUses()
    {
        var tenantId = TestIds.New("wl-loc");
        await using var ctx = fixture.CreateContext(tenantId);
        var handler = BuildEnroll(ctx);

        await handler.Handle(EnrollCommandFor(tenantId), TestContext.Current.CancellationToken);
        await handler.Handle(
            EnrollCommandFor(tenantId) with { LocationId = "loc-2", PartySize = 3 },
            TestContext.Current.CancellationToken);
        // A repeat of an existing location must not appear twice.
        await handler.Handle(
            EnrollCommandFor(tenantId) with { PartySize = 5 },
            TestContext.Current.CancellationToken);

        var locations = await new WaitlistEnrollmentRepository(ctx).ListLocationsInUseAsync(tenantId);

        Assert.Equal(new[] { "loc-1", "loc-2" }, locations);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ANewVenueHasNoLocationsYetAndThatIsARealAnswer()
    {
        // Which is exactly why an unrecognised location must WARN rather than block: the first
        // enrolment at a venue can never be in the list.
        var tenantId = TestIds.New("wl-loc-empty");
        await using var ctx = fixture.CreateContext(tenantId);

        Assert.Empty(await new WaitlistEnrollmentRepository(ctx).ListLocationsInUseAsync(tenantId));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DoesNotListAnotherTenantsLocations()
    {
        var tenantA = TestIds.New("wl-loc-a");
        var tenantB = TestIds.New("wl-loc-b");
        await using (var ctxA = fixture.CreateContext(tenantA))
            await BuildEnroll(ctxA).Handle(EnrollCommandFor(tenantA), TestContext.Current.CancellationToken);

        await using var ctxB = fixture.CreateContext(tenantB);
        Assert.Empty(await new WaitlistEnrollmentRepository(ctxB).ListLocationsInUseAsync(tenantB));
    }
}
