#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Application.Handlers.Waitlist;
using Cordia.Application.Handlers.Waitlist.Accept;
using Cordia.Application.Handlers.Waitlist.AutoAccept;
using Cordia.Application.Handlers.Waitlist.Cancel;
using Cordia.Application.Handlers.Waitlist.Decline;
using Cordia.Application.Handlers.Waitlist.Enroll;
using Cordia.Application.Handlers.Waitlist.Expire;
using Cordia.Application.Handlers.Waitlist.Match;
using Cordia.Application.Handlers.Waitlist.ReconcileAccept;
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
            new FirstPartyCapacityReservation(ctx, new CapacityHoldRepository(ctx)),
            new OfferExpiryScheduler(new ScheduledJobRepository(ctx)),
            new OutboxRepository(ctx),
            new AuditEventRepository(ctx),
            NullLogger<MatchWaitlistOfferCommandHandler>.Instance);

    // Spec 11 §5.1 (divergence #1): accept now routes through the spec 04 → 06 front door via IMediator
    // rather than finalizing its own hold. Driving that end-to-end from a hand-built handler needs a real
    // mediator wired to the 04 handler + capacity/06 stack — the Phase-6 integration rewrite. The only
    // remaining caller of this builder is the (skipped) obsolete accept test; the null! mediator is never
    // dereferenced until that test is re-authored.
    private static AcceptWaitlistOfferCommandHandler BuildAccept(ApplicationDbContext ctx)
        => new(
            new WaitlistOfferRepository(ctx),
            new WaitlistEnrollmentRepository(ctx),
            new CanonicalRequestHasher(),
            null!, // IMediator — Phase 6 (04→06 wiring)
            new AuditEventRepository(ctx),
            new WaitlistAcceptReconcileScheduler(new ScheduledJobRepository(ctx)),
            new UnitOfWork(ctx));

    private static DeclineWaitlistOfferCommandHandler BuildDecline(ApplicationDbContext ctx)
        => new(
            new WaitlistOfferRepository(ctx),
            new WaitlistEnrollmentRepository(ctx),
            new FirstPartyCapacityReservation(ctx, new CapacityHoldRepository(ctx)),
            new OutboxRepository(ctx),
            new AuditEventRepository(ctx),
            new UnitOfWork(ctx));

    private static ExpireWaitlistOfferCommandHandler BuildExpire(ApplicationDbContext ctx)
        => new(
            new WaitlistOfferRepository(ctx),
            new WaitlistEnrollmentRepository(ctx),
            new FirstPartyCapacityReservation(ctx, new CapacityHoldRepository(ctx)),
            new OutboxRepository(ctx),
            new AuditEventRepository(ctx),
            NullLogger<ExpireWaitlistOfferCommandHandler>.Instance);

    private static EnrollWaitlistCommandHandler BuildEnroll(ApplicationDbContext ctx)
        => new(new WaitlistEnrollmentRepository(ctx), new AuditEventRepository(ctx), new UnitOfWork(ctx));

    private static CancelWaitlistEnrollmentCommandHandler BuildCancel(ApplicationDbContext ctx)
        => new(
            new WaitlistEnrollmentRepository(ctx),
            new WaitlistOfferRepository(ctx),
            new FirstPartyCapacityReservation(ctx, new CapacityHoldRepository(ctx)),
            new OutboxRepository(ctx),
            new AuditEventRepository(ctx),
            new UnitOfWork(ctx));

    // ── Seeding helpers ────────────────────────────────────────────────────────────────────────

    private async Task<string> SeedSlotAsync(
        string tenantId, DateTimeOffset startsAt, int total, int held)
    {
        var resourceId = $"res-{Guid.NewGuid():N}";
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
        var enrollmentId = $"wle_{Guid.NewGuid():N}";
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
        var offerId = $"wlo_{Guid.NewGuid():N}";
        var holdId = $"hold_{Guid.NewGuid():N}";
        var operationId = $"op_wl_{Guid.NewGuid():N}";
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
        => new(tenantId, $"msg-{Guid.NewGuid():N}", slotId, "loc-1", "svc-1", slotId, slotStart, seats);

    // ── Enroll / Cancel ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Enroll_PersistsPendingEnrollment_AndAudits()
    {
        var tenantId = $"wl-enroll-{Guid.NewGuid():N}";
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
        var tenantId = $"wl-cancel-{Guid.NewGuid():N}";
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart);

        await using (var ctx = fixture.CreateContext(tenantId))
            await BuildCancel(ctx).Handle(new CancelWaitlistEnrollmentCommand(tenantId, enrollmentId, "customer"), CancellationToken.None);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var e = await ctx.WaitlistEnrollments.AsNoTracking().FirstAsync(x => x.EnrollmentId == enrollmentId);
            Assert.NotNull(e.CancelledAt);
        }

        // Idempotent re-cancel: no throw.
        await using (var ctx = fixture.CreateContext(tenantId))
            await BuildCancel(ctx).Handle(new CancelWaitlistEnrollmentCommand(tenantId, enrollmentId, "customer"), CancellationToken.None);

        // Unknown enrollment → 404.
        await using (var ctx = fixture.CreateContext(tenantId))
            await Assert.ThrowsAsync<WaitlistEnrollmentNotFoundException>(() =>
                BuildCancel(ctx).Handle(new CancelWaitlistEnrollmentCommand(tenantId, "wle_missing", "customer"), CancellationToken.None));
    }

    [Fact]
    public async Task Cancel_WithLiveOffer_ReleasesHold_AndEnqueuesRematch()
    {
        // Review 2026-07-03 C10: cancelling an enrollment that holds a live offer must re-match
        // the freed opening to the next eligible enrollee, exactly like decline/expire (§5.1/FM#1)
        // — otherwise the slot silently leaves the waitlist queue.
        var tenantId = $"wl-cancel-live-{Guid.NewGuid():N}";
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 4, held: 2);
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2);
        var (offerId, holdId, _) = await SeedOfferedAsync(tenantId, enrollmentId, slotId, slotStart, seats: 4, reserved: 2);

        await using (var ctx = fixture.CreateContext(tenantId))
            await BuildCancel(ctx).Handle(new CancelWaitlistEnrollmentCommand(tenantId, enrollmentId, "customer"), CancellationToken.None);

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
        var tenantId = $"wl-match-{Guid.NewGuid():N}";
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
        var tenantId = $"wl-autobook-{Guid.NewGuid():N}";
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
        var tenantId = $"wl-nocap-{Guid.NewGuid():N}";
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 2, held: 2); // full
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2);

        await RunMatchAsync(tenantId, MatchCmd(tenantId, slotId, slotStart, seats: 2));

        await using var ctx = fixture.CreateContext(tenantId);
        Assert.False(await ctx.WaitlistOffers.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(o => o.TenantId == tenantId && o.EnrollmentId == enrollmentId));
    }

    // ── Accept ───────────────────────────────────────────────────────────────────────────────

    [Fact(Skip = "Phase 6: rewrite for the spec 04 → 06 accept path (divergence #1). This asserts the " +
                 "obsolete direct-finalize + bespoke waitlist.booking_confirmed.v1 behavior; the new flow " +
                 "mints booking_intent_revisions/intent_confirmations/booking_operations via spec 04 and " +
                 "lets the 06 engine finalize the reused capacity hold.")]
    public async Task Accept_FinalizesHold_ConfirmsBooking_AndIsIdempotent()
    {
        var tenantId = $"wl-accept-{Guid.NewGuid():N}";
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 4, held: 2);
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2);
        var (offerId, holdId, _) = await SeedOfferedAsync(tenantId, enrollmentId, slotId, slotStart, seats: 4, reserved: 2);

        await using (var ctx = fixture.CreateContext(tenantId))
            await BuildAccept(ctx).Handle(new AcceptWaitlistOfferCommand(tenantId, offerId, "customer"), CancellationToken.None);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var offer = await ctx.WaitlistOffers.AsNoTracking().IgnoreQueryFilters().SingleAsync(o => o.OfferId == offerId);
            Assert.Equal(WaitlistOfferStatus.Accepted, offer.Status);

            var hold = await ctx.CapacityHolds.AsNoTracking().IgnoreQueryFilters().SingleAsync(h => h.HoldId == holdId);
            Assert.Equal(HoldStatus.Finalized, hold.Status);

            var slot = await ctx.CapacitySlots.AsNoTracking().IgnoreQueryFilters().SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.HeldCapacity);
            Assert.Equal(2, slot.BookedCapacity);

            Assert.True(await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(m => m.TenantId == tenantId && m.MessageType == "waitlist.booking_confirmed.v1"));
        }

        // Idempotent: re-accept is a no-op (slot not double-moved).
        await using (var ctx = fixture.CreateContext(tenantId))
            await BuildAccept(ctx).Handle(new AcceptWaitlistOfferCommand(tenantId, offerId, "customer"), CancellationToken.None);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var slot = await ctx.CapacitySlots.AsNoTracking().IgnoreQueryFilters().SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(2, slot.BookedCapacity);
        }

        // Unknown offer → 404.
        await using (var ctx = fixture.CreateContext(tenantId))
            await Assert.ThrowsAsync<WaitlistOfferNotFoundException>(() =>
                BuildAccept(ctx).Handle(new AcceptWaitlistOfferCommand(tenantId, "wlo_missing", "customer"), CancellationToken.None));
    }

    // ── Decline ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Decline_ReleasesHold_AndEnqueuesRematch()
    {
        var tenantId = $"wl-decline-{Guid.NewGuid():N}";
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 4, held: 2);
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2);
        var (offerId, holdId, _) = await SeedOfferedAsync(tenantId, enrollmentId, slotId, slotStart, seats: 4, reserved: 2);

        await using (var ctx = fixture.CreateContext(tenantId))
            await BuildDecline(ctx).Handle(new DeclineWaitlistOfferCommand(tenantId, offerId, "customer"), CancellationToken.None);

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
        var tenantId = $"wl-expire-{Guid.NewGuid():N}";
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 4, held: 2);
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2);
        var (offerId, holdId, _) = await SeedOfferedAsync(tenantId, enrollmentId, slotId, slotStart, seats: 4, reserved: 2);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var handler = BuildExpire(ctx);
            await using var tx = await ctx.Database.BeginTransactionAsync();
            await handler.Handle(new ExpireWaitlistOfferCommand(tenantId, $"msg-{Guid.NewGuid():N}", null, offerId), CancellationToken.None);
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
        var tenantId = $"wl-expire-noop-{Guid.NewGuid():N}";
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
            await handler.Handle(new ExpireWaitlistOfferCommand(tenantId, $"msg-{Guid.NewGuid():N}", null, offerId), CancellationToken.None);
            await tx.CommitAsync();
        }

        await using var assertCtx = fixture.CreateContext(tenantId);
        var offer = await assertCtx.WaitlistOffers.AsNoTracking().IgnoreQueryFilters().SingleAsync(o => o.OfferId == offerId);
        Assert.Equal(WaitlistOfferStatus.Accepted, offer.Status); // unchanged — expiry was a no-op
    }
}
