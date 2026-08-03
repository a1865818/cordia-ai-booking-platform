#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.ManageBooking;

[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class SubmitBookingCancellationEndpointTests(AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task SubmitCancellation_WithValidGrant_DurablyAcceptsWithoutDeletingBooking()
    {
        var tenantId = $"t8-can-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-can", "cust-can", "grant-can", VerificationActionEnum.Cancel);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-can", 3, "op-can", "idem-can", "grant-can")).ToUrl("/api/bookings/bk-can/cancellations");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal("accepted", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("op-can", doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("/api/booking-operations/op-can", doc.RootElement.GetProperty("status_url").GetString());

        await using var ctx = fixture.CreateContext(tenantId);
        var booking = await ctx.Bookings.AsNoTracking().SingleAsync(b => b.BookingId == "bk-can");
        Assert.Equal(BookingLifecycleStatus.CancellationPending, booking.LifecycleStatus);
        Assert.Equal(4, booking.Version);

        var operation = await ctx.BookingOperations.AsNoTracking().SingleAsync(o => o.OperationId == "op-can");
        Assert.Equal(OperationTypeEnum.Cancel, operation.OperationType);
        Assert.Equal(OperationStatus.Accepted, operation.Status);
        Assert.Equal("bk-can", operation.BookingId);

        var revision = await ctx.BookingIntentRevisions.AsNoTracking()
            .SingleAsync(r => r.IntentId == "intent_op-can" && r.Revision == 1);
        using (var revisionDoc = JsonDocument.Parse(revision.CanonicalPayload))
        {
            Assert.Equal("bk-can", revisionDoc.RootElement.GetProperty("target_booking_id").GetString());
            Assert.Equal("customer_request", revisionDoc.RootElement.GetProperty("reason_code").GetString());
        }

        Assert.False(await ctx.IntentConfirmations.AsNoTracking().AnyAsync(c => c.IntentId == "intent_op-can"));

        var outbox = await ctx.OutboxMessages.AsNoTracking().SingleAsync(o => o.OperationId == "op-can");
        Assert.Equal("booking.command.requested.v1", outbox.MessageType);

        var audit = await ctx.AuditEvents.AsNoTracking().SingleAsync(a => a.OperationId == "op-can");
        Assert.Equal("booking_cancellation_accepted", audit.EventType);
    }

    [Fact]
    public async Task SubmitCancellation_ReplaySameIdempotencyKey_ReturnsDuplicateAccepted()
    {
        var tenantId = $"t8-can-replay-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-replay", "cust-replay", "grant-replay", VerificationActionEnum.Cancel);
        var body = Body("bk-replay", 3, "op-replay", "idem-replay", "grant-replay");

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl("/api/bookings/bk-replay/cancellations");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var replay = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl("/api/bookings/bk-replay/cancellations");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        using var doc = JsonDocument.Parse(await replay.ReadAsTextAsync());
        Assert.Equal("duplicate_accepted", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("op-replay", doc.RootElement.GetProperty("operation_id").GetString());
    }

    [Fact]
    public async Task SubmitCancellation_SameKeyDifferentBody_ReturnsIdempotencyConflict()
    {
        var tenantId = $"t8-can-conflict-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-conflict", "cust-conflict", "grant-conflict", VerificationActionEnum.Cancel);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-conflict", 3, "op-conflict", "idem-conflict", "grant-conflict")).ToUrl("/api/bookings/bk-conflict/cancellations");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var conflict = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-conflict", 3, "op-conflict-2", "idem-conflict", "grant-conflict")).ToUrl("/api/bookings/bk-conflict/cancellations");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });

        using var doc = JsonDocument.Parse(await conflict.ReadAsTextAsync());
        Assert.Equal("idempotency_conflict", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SubmitCancellation_StaleVersion_ReturnsStaleVersionAndDoesNotWriteOperation()
    {
        var tenantId = $"t8-can-stale-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-stale", "cust-stale", "grant-stale", VerificationActionEnum.Cancel);

        var stale = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-stale", 2, "op-stale", "idem-stale", "grant-stale")).ToUrl("/api/bookings/bk-stale/cancellations");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });

        using var doc = JsonDocument.Parse(await stale.ReadAsTextAsync());
        Assert.Equal("stale_version", doc.RootElement.GetProperty("status").GetString());

        await using var ctx = fixture.CreateContext(tenantId);
        Assert.False(await ctx.BookingOperations.AsNoTracking().AnyAsync(o => o.OperationId == "op-stale"));
        Assert.False(await ctx.IdempotencyRecords.AsNoTracking().AnyAsync(r => r.IdempotencyKey == "idem-stale"));
        var booking = await ctx.Bookings.AsNoTracking().SingleAsync(b => b.BookingId == "bk-stale");
        Assert.Equal(BookingLifecycleStatus.Confirmed, booking.LifecycleStatus);
        Assert.Equal(3, booking.Version);
    }

    [Fact]
    public async Task SubmitCancellation_FreshKeyAgainstCancelledBooking_ReturnsAlreadyCancelledWithoutNewOperation()
    {
        var tenantId = $"t8-can-already-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(
            tenantId,
            "bk-already",
            "cust-already",
            "grant-already",
            VerificationActionEnum.Cancel,
            BookingLifecycleStatus.Cancelled);

        var already = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-already", 3, "op-already", "idem-already", "grant-already")).ToUrl("/api/bookings/bk-already/cancellations");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });

        using var doc = JsonDocument.Parse(await already.ReadAsTextAsync());
        Assert.Equal("already_cancelled", doc.RootElement.GetProperty("status").GetString());

        await using var ctx = fixture.CreateContext(tenantId);
        Assert.False(await ctx.BookingOperations.AsNoTracking().AnyAsync(o => o.OperationId == "op-already"));
        Assert.False(await ctx.IdempotencyRecords.AsNoTracking().AnyAsync(r => r.IdempotencyKey == "idem-already"));
    }

    [Fact]
    public async Task SubmitCancellation_WrongGrantAction_Returns403()
    {
        var tenantId = $"t8-can-grant-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-grant", "cust-grant", "grant-modify", VerificationActionEnum.Modify);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-grant", 3, "op-grant", "idem-grant", "grant-modify")).ToUrl("/api/bookings/bk-grant/cancellations");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task SubmitCancellation_EmptyGrant_Is400_NotAVerificationPrompt()
    {
        // The console's historical request shape. `grant_id` is NotEmpty-validated, so an empty
        // grant never reaches the authorization spine — it is a 400 malformed request, NOT a 403
        // the operator can resolve by verifying. A client cannot "recover" from this; it has to
        // obtain the grant BEFORE submitting.
        var tenantId = $"t8-can-empty-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-empty", "cust-empty", "grant-empty", VerificationActionEnum.Cancel);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-empty", 3, "op-empty", "idem-empty", grantId: "")).ToUrl("/api/bookings/bk-empty/cancellations");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task SubmitCancellation_WrongGrant_Returns403WithAResolvableDiscriminator()
    {
        // A present-but-wrong grant IS resolvable: re-run step-up for the right action. `title` is
        // prose for a human; the extensions are what a client can branch on.
        var tenantId = $"t8-can-disc-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-disc", "cust-disc", "grant-disc", VerificationActionEnum.Modify);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-disc", 3, "op-disc", "idem-disc", "grant-disc")).ToUrl("/api/bookings/bk-disc/cancellations");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });

        using var problem = JsonDocument.Parse(await result.ReadAsTextAsync());
        var root = problem.RootElement;

        Assert.Equal("verification_required", root.GetProperty("code").GetString());
        Assert.Equal("cancel", root.GetProperty("action").GetString());
        Assert.Equal("bk-disc", root.GetProperty("target_booking_id").GetString());

        // The customer identity is exactly what the caller has not yet proven a right to see.
        Assert.False(root.TryGetProperty("customer_ref", out _));
    }

    [Fact]
    public async Task SubmitCancellation_WrongGrant_LeavesTheBookingAndTheKeyUntouched()
    {
        // The grant check runs INSIDE the transaction that already flipped the booking to
        // cancellation_pending and elected the idempotency record. If the rollback were incomplete,
        // a refused cancel would strand the booking mid-cancellation and burn the key.
        var tenantId = $"t8-can-roll-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-roll", "cust-roll", "grant-roll", VerificationActionEnum.Modify);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-roll", 3, "op-roll", "idem-roll", "grant-roll")).ToUrl("/api/bookings/bk-roll/cancellations");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });

        await using var ctx = fixture.CreateContext(tenantId);
        var booking = await ctx.Bookings.AsNoTracking()
            .SingleAsync(b => b.TenantId == tenantId && b.BookingId == "bk-roll");
        Assert.Equal(BookingLifecycleStatus.Confirmed, booking.LifecycleStatus);
        Assert.Equal(3, booking.Version);

        // The key is free, so a retry with the RIGHT grant may reuse it (Inv-2) even though
        // changing grant_id changes the canonical request hash.
        Assert.False(await ctx.IdempotencyRecords.AsNoTracking()
            .AnyAsync(r => r.TenantId == tenantId && r.IdempotencyKey == "idem-roll"));
        Assert.False(await ctx.BookingOperations.AsNoTracking()
            .AnyAsync(o => o.TenantId == tenantId && o.OperationId == "op-roll"));
    }

    private async Task SeedBookingAndGrantAsync(
        string tenantId,
        string bookingId,
        string customerId,
        string grantId,
        VerificationActionEnum grantAction,
        BookingLifecycleStatus lifecycle = BookingLifecycleStatus.Confirmed)
    {
        var now = DateTimeOffset.UtcNow;
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.Bookings.Add(new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = $"CORD-{bookingId}",
            Version = 3,
            LifecycleStatus = lifecycle,
            CustomerId = customerId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        ctx.VerificationGrants.Add(new VerificationGrant
        {
            TenantId = tenantId,
            GrantId = grantId,
            CustomerRef = customerId,
            TargetBookingId = bookingId,
            AllowedAction = grantAction,
            VerificationLevel = VerificationLevel.IdentityVerified,
            SessionOrOperationRef = bookingId,
            IssuedAt = now,
            ExpiresAt = now.AddMinutes(15),
        });
        await ctx.SaveChangesAsync();
    }

    private static string Body(string bookingId, long version, string operationId, string idempotencyKey, string grantId)
        => $$"""
        {
          "target_booking_id": "{{bookingId}}",
          "expected_booking_version": {{version}},
          "operation_id": "{{operationId}}",
          "cancellation_idempotency_key": "{{idempotencyKey}}",
          "reason_code": "customer_request",
          "grant_id": "{{grantId}}"
        }
        """;
}
