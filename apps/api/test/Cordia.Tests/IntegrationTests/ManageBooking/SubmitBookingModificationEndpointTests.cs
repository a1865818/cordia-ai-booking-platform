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
public class SubmitBookingModificationEndpointTests(AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task SubmitModification_WithValidGrant_DurablyAcceptsWithoutDeletingBooking()
    {
        var tenantId = $"t8-mod-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-mod", "cust-mod", "grant-mod", VerificationActionEnum.Modify);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-mod", 3, "op-mod", "idem-mod", "grant-mod")).ToUrl("/api/bookings/bk-mod/modifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal("accepted", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("op-mod", doc.RootElement.GetProperty("operation_id").GetString());
        Assert.Equal("/api/booking-operations/op-mod", doc.RootElement.GetProperty("status_url").GetString());

        await using var ctx = fixture.CreateContext(tenantId);
        var booking = await ctx.Bookings.AsNoTracking().SingleAsync(b => b.BookingId == "bk-mod");
        Assert.Equal(BookingLifecycleStatus.ModificationPending, booking.LifecycleStatus);
        Assert.Equal(4, booking.Version);

        var operation = await ctx.BookingOperations.AsNoTracking().SingleAsync(o => o.OperationId == "op-mod");
        Assert.Equal(OperationTypeEnum.Modify, operation.OperationType);
        Assert.Equal(OperationStatus.Accepted, operation.Status);
        Assert.Equal("bk-mod", operation.BookingId);

        var revision = await ctx.BookingIntentRevisions.AsNoTracking()
            .SingleAsync(r => r.IntentId == "intent-op-mod" && r.Revision == 1);
        Assert.Equal("hash-op-mod", revision.PayloadHash);
        using (var revisionDoc = JsonDocument.Parse(revision.CanonicalPayload))
        {
            Assert.Equal("bk-mod", revisionDoc.RootElement.GetProperty("target_booking_id").GetString());
        }

        var outbox = await ctx.OutboxMessages.AsNoTracking().SingleAsync(o => o.OperationId == "op-mod");
        Assert.Equal("booking.command.requested.v1", outbox.MessageType);

        var audit = await ctx.AuditEvents.AsNoTracking().SingleAsync(a => a.OperationId == "op-mod");
        Assert.Equal("booking_modification_accepted", audit.EventType);
    }

    [Fact]
    public async Task SubmitModification_ReplaySameIdempotencyKey_ReturnsDuplicateAccepted()
    {
        var tenantId = $"t8-mod-replay-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-replay", "cust-replay", "grant-replay", VerificationActionEnum.Modify);
        var body = Body("bk-replay", 3, "op-replay", "idem-replay", "grant-replay");

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl("/api/bookings/bk-replay/modifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var replay = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl("/api/bookings/bk-replay/modifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        using var doc = JsonDocument.Parse(await replay.ReadAsTextAsync());
        Assert.Equal("duplicate_accepted", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("op-replay", doc.RootElement.GetProperty("operation_id").GetString());
    }

    [Fact]
    public async Task SubmitModification_SameKeyDifferentBody_ReturnsIdempotencyConflict()
    {
        var tenantId = $"t8-mod-conflict-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-conflict", "cust-conflict", "grant-conflict", VerificationActionEnum.Modify);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-conflict", 3, "op-conflict", "idem-conflict", "grant-conflict")).ToUrl("/api/bookings/bk-conflict/modifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        var conflict = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-conflict", 3, "op-conflict-2", "idem-conflict", "grant-conflict")).ToUrl("/api/bookings/bk-conflict/modifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });

        using var doc = JsonDocument.Parse(await conflict.ReadAsTextAsync());
        Assert.Equal("idempotency_conflict", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task SubmitModification_StaleVersion_ReturnsStaleVersionAndDoesNotWriteOperation()
    {
        var tenantId = $"t8-mod-stale-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-stale", "cust-stale", "grant-stale", VerificationActionEnum.Modify);

        var stale = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-stale", 2, "op-stale", "idem-stale", "grant-stale")).ToUrl("/api/bookings/bk-stale/modifications");
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
    public async Task SubmitModification_WrongGrantAction_Returns403()
    {
        var tenantId = $"t8-mod-grant-{Guid.NewGuid():N}";
        await SeedBookingAndGrantAsync(tenantId, "bk-grant", "cust-grant", "grant-cancel", VerificationActionEnum.Cancel);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(Body("bk-grant", 3, "op-grant", "idem-grant", "grant-cancel")).ToUrl("/api/bookings/bk-grant/modifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    private async Task SeedBookingAndGrantAsync(
        string tenantId,
        string bookingId,
        string customerId,
        string grantId,
        VerificationActionEnum grantAction)
    {
        var now = DateTimeOffset.UtcNow;
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.Bookings.Add(new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = $"CORD-{bookingId}",
            Version = 3,
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
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
          "modification_idempotency_key": "{{idempotencyKey}}",
          "intent_revision": 1,
          "intent_payload_hash": "hash-{{operationId}}",
          "confirmation": {
            "confirmation_channel": "web",
            "confirmation_id": "conf-{{operationId}}",
            "confirmed_at": "2026-06-29T00:00:00Z",
            "disclosures_presented": ["manage_booking_terms"],
            "identity": "customer",
            "intent_id": "intent-{{operationId}}",
            "intent_payload_hash": "hash-{{operationId}}",
            "intent_revision": 1,
            "read_back_version": "rb-v1",
            "source_turn_id": "turn-{{operationId}}",
            "verification_level": "identity_verified"
          },
          "grant_id": "{{grantId}}"
        }
        """;
}
