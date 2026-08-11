#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using System.Net;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Waitlist;

/// <summary>
/// The waitlist ACCEPT path (spec 11 §5.1, divergence #1), driven through the real endpoint.
///
/// <para><b>Why this lives apart from <c>WaitlistFlowTests</c>.</b> Accept no longer books
/// anything itself: it builds a canonical <c>BookingRequest</c> from the enrollment and submits it
/// through the spec 04 front door, which mints the official records and hands the booking to the
/// spec 06 engine. That needs a real <c>IMediator</c>, and <c>WaitlistFlowTests</c> hand-builds
/// its handlers over a bare <c>PostgresFixture</c> with no host — it passed <c>null!</c> for the
/// mediator, which is exactly why its accept test could only be skipped rather than fixed. Here
/// the Alba host supplies the whole graph, so the assertions are about the shipped wiring.
///
/// <para><b>What the negative assertions are for.</b> The old behaviour finalized the capacity
/// hold inside the accept and emitted a bespoke <c>waitlist.booking_confirmed.v1</c>. That was a
/// parallel booking path: resolving the offer minted the booking, so anyone who saw the offer
/// notification could effectively book for the enrollee (Inv-1). Asserting those things do NOT
/// happen is how that path stays closed.</para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class WaitlistAcceptFlowTests(AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task Accept_MintsTheConfirmedIntentThroughSpec04_AndIsIdempotent()
    {
        var tenantId = TestIds.New("wl-accept");
        var slotStart = DateTimeOffset.UtcNow.AddDays(1);
        var slotId = await SeedSlotAsync(tenantId, slotStart, total: 4, held: 2);
        var enrollmentId = await SeedEnrollmentAsync(tenantId, slotStart, partySize: 2);
        var (offerId, holdId, operationId) =
            await SeedOfferedAsync(tenantId, enrollmentId, slotId, slotStart, seats: 4, reserved: 2);

        await AcceptAsync(tenantId, offerId);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var offer = await ctx.WaitlistOffers.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(o => o.OfferId == offerId);
            Assert.Equal(WaitlistOfferStatus.Accepted, offer.Status);

            // Spec 04 minted the OFFICIAL records, under the offer's own operation id (Inv-1).
            var operation = await ctx.BookingOperations.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

            Assert.True(await ctx.BookingIntentRevisions.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(r => r.TenantId == tenantId
                    && r.IntentId == operation.IntentId
                    && r.Revision == operation.IntentRevision));

            Assert.True(await ctx.IntentConfirmations.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(c => c.TenantId == tenantId && c.IntentId == operation.IntentId));

            // The accept does NOT finalize the hold. The 06 engine reuses it under the `create`
            // workflow step and finalizes it when the booking actually lands — settling it here
            // would book capacity for an operation that may still fail in payment or at the
            // provider.
            var hold = await ctx.CapacityHolds.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(h => h.HoldId == holdId);
            Assert.NotEqual(HoldStatus.Finalized, hold.Status);

            var slot = await ctx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.BookedCapacity);

            // The bespoke parallel-path event is gone: spec 06 emits the single confirmed outcome.
            Assert.False(await ctx.OutboxMessages.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(m => m.TenantId == tenantId && m.MessageType == "waitlist.booking_confirmed.v1"));

            Assert.True(await ctx.AuditEvents.AsNoTracking().IgnoreQueryFilters()
                .AnyAsync(e => e.TenantId == tenantId
                    && e.EventType == "waitlist.accepted"
                    && e.SubjectRef == offerId));
        }

        // Idempotent (Inv-5): re-accepting replays through spec 04's deterministic per-offer key
        // and must not mint a second booking or move capacity again.
        await AcceptAsync(tenantId, offerId);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            Assert.Equal(1, await ctx.BookingOperations.AsNoTracking().IgnoreQueryFilters()
                .CountAsync(o => o.TenantId == tenantId && o.OperationId == operationId));

            var slot = await ctx.CapacitySlots.AsNoTracking().IgnoreQueryFilters()
                .SingleAsync(s => s.SlotId == slotId);
            Assert.Equal(0, slot.BookedCapacity);
        }
    }

    /// <summary>An unknown offer is a 404, not a silently-successful no-op.</summary>
    [Fact]
    public async Task Accept_UnknownOffer_Returns404()
    {
        var tenantId = TestIds.New("wl-accept-404");

        await fixture.Host.Scenario(x =>
        {
            x.Post.Url($"/api/waitlist/offers/{TestIds.New("wlo_missing")}/accept");
            Actor(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.NotFound);
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private async Task AcceptAsync(string tenantId, string offerId)
        => await fixture.Host.Scenario(x =>
        {
            x.Post.Url($"/api/waitlist/offers/{offerId}/accept");
            Actor(x, tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

    /// <summary>
    /// Every waitlist mutation is an operator action (spec 14 §24.5): without the actor trio
    /// <c>OperatorActionBehavior</c> deny-audits and 403s before the handler runs.
    /// </summary>
    private static void Actor(Alba.Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "console-operator");
        x.WithRequestHeader("X-Actor-Role", "operator");
        x.WithRequestHeader("X-Reason-Code", "waitlist_accept");
    }

    private async Task<string> SeedSlotAsync(string tenantId, DateTimeOffset startsAt, int total, int held)
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

    private async Task<string> SeedEnrollmentAsync(string tenantId, DateTimeOffset slotStart, int partySize)
    {
        var enrollmentId = TestIds.New("wle_");
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.WaitlistEnrollments.Add(new WaitlistEnrollment
        {
            TenantId = tenantId,
            EnrollmentId = enrollmentId,
            CustomerRef = "cust-1",
            // What EnrollWaitlistCommandHandler stores: the captured identity, falling back to the
            // customer ref. The accept attributes its confirmation to THIS, so the booking is bound
            // to the enrolled customer rather than to whoever opened the offer notification
            // (divergence #1) — and spec 04 rejects a confirmation with no identity outright.
            IdentityRef = "cust-1",
            AcceptableFrom = slotStart.AddHours(-2),
            AcceptableTo = slotStart.AddHours(2),
            PartySize = partySize,
            LocationId = "loc-1",
            ServiceId = "svc-1",
            OfferLifetimeSeconds = 900,
            NotificationChannel = "email",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return enrollmentId;
    }

    private async Task<(string offerId, string holdId, string operationId)> SeedOfferedAsync(
        string tenantId, string enrollmentId, string slotId, DateTimeOffset slotStart, int seats, int reserved)
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
}
