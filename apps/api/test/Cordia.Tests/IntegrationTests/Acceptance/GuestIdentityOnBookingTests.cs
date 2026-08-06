#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Alba;
using Cordia.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// A booking submitted through the REAL front door must carry a guest identity that survives
/// into storage, and must then be discoverable by phone.
///
/// These tests deliberately drive <c>POST /api/bookings</c> rather than hand-seeding a
/// <c>canonical_payload</c> row. The pre-existing phone-lookup fixtures
/// (<c>ManageBookingReadEndpointTests.SeedManageableBookingAsync</c>,
/// <c>BookingReadRepositoryTests</c>) seed <c>booking_id</c>, <c>business_name</c>,
/// <c>booking_date</c>, <c>approx_time</c> and a nested <c>customer</c> — not one of which the
/// submission path can produce — so they pass while the feature does not work. Driving the real
/// endpoint is the only way to keep that from happening again.
///
/// G1 — a payload carrying a guest is accepted (202) rather than rejected on hash mismatch.
/// G2 — the accepted booking is findable via <c>GET /api/bookings?phone_e164=</c>.
/// G3 — the stored canonical payload still hashes to <c>payload_hash</c> (no sanitiser mutates it).
/// G4 — a guest phone is scoped to its tenant.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class GuestIdentityOnBookingTests(AcceptanceApiFixture fixture)
{
    /// <summary>
    /// Builds a <c>POST /api/bookings</c> body whose canonical hash is computed over the payload
    /// JSON <em>exactly as sent</em>.
    ///
    /// This mirrors the real clients: <c>buildSubmission.ts</c> hashes the object it is about to
    /// send, and the Python agent hashes the <c>BookingRequest</c> it submits. The server instead
    /// re-serializes its own deserialized <c>BookingRequest</c> before hashing
    /// (<c>SubmitConfirmedIntentCommandHandler.cs:58</c>), so any field the generated type cannot
    /// hold is dropped and the two hashes diverge — which is precisely what these tests pin.
    /// </summary>
    private static (string Body, string PayloadHash) BuildRequestWithGuest(
        string operationId,
        string idempotencyKey,
        string intentId,
        string serviceId,
        string? phoneE164,
        string? customerId = null)
    {
        var customerBlock = phoneE164 is null
            ? string.Empty
            : $$"""
              ,"customer": {"customer_id": "{{customerId ?? "cust-" + Guid.NewGuid().ToString("N")[..8]}}", "phone_e164": "{{phoneE164}}"}
              """;

        var payloadJson = $$"""
        {
          "operation": "create",
          "service_id": "{{serviceId}}",
          "location_id": "",
          "target_booking_id": "",
          "starts_at": "2026-09-01T09:00:00.000Z",
          "party_size": 2,
          "special_requests": []{{customerBlock}}
        }
        """;

        // Hash what the client sends — not what the server can hold.
        var hash = new CanonicalRequestHasher().Hash(JsonNode.Parse(payloadJson)).Value;
        var confirmationId = Guid.NewGuid().ToString();

        var body = $$"""
        {
          "operation_id": "{{operationId}}",
          "idempotency_key": "{{idempotencyKey}}",
          "intent_id": "{{intentId}}",
          "intent_revision": 1,
          "replaces_operation_id": "",
          "revision": {
            "intent_id": "{{intentId}}",
            "revision": 1,
            "canonical_payload": {{payloadJson}},
            "schema_version": "intent.v1",
            "tenant_policy_version": "policy.v1",
            "payload_hash": "{{hash}}",
            "created_at": "2026-07-01T00:00:00.000Z",
            "source_session_id": "sess-guest",
            "supersession_reason": ""
          },
          "confirmation": {
            "confirmation_id": "{{confirmationId}}",
            "intent_id": "{{intentId}}",
            "intent_revision": 1,
            "intent_payload_hash": "{{hash}}",
            "identity": "operator:console",
            "confirmation_channel": "web",
            "confirmed_at": "2026-07-01T00:00:00.000Z",
            "source_turn_id": "turn-guest",
            "read_back_version": "rb.v1",
            "disclosures_presented": [],
            "verification_level": "unverified"
          }
        }
        """;

        return (body, hash);
    }

    private async Task<string> SubmitWithGuestAsync(
        string tenantId,
        string phoneE164,
        string serviceId = "svc-guest",
        string? customerId = null)
    {
        var operationId = $"op-{Guid.NewGuid():N}";
        var (body, _) = BuildRequestWithGuest(
            operationId,
            $"ik-{Guid.NewGuid():N}",
            $"int-{Guid.NewGuid():N}",
            serviceId,
            phoneE164,
            customerId);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        return operationId;
    }

    // -------------------------------------------------------------------------
    // G1 — a guest-carrying payload is accepted, not rejected on hash mismatch.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Post_PayloadCarryingGuest_IsAcceptedAndStoresTheGuest()
    {
        var tenantId = $"guest-g1-{Guid.NewGuid():N}";
        const string phone = "+61400111222";
        var operationId = await SubmitWithGuestAsync(tenantId, phone);

        await using var ctx = fixture.CreateContext(tenantId);

        var operation = await ctx.BookingOperations
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.NotNull(operation);

        var revision = await ctx.BookingIntentRevisions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.IntentId == operation.IntentId);
        Assert.NotNull(revision);

        // The guest must have survived deserialization into storage — not been silently dropped.
        Assert.Contains(phone, revision.CanonicalPayload);
    }

    // -------------------------------------------------------------------------
    // G2 — the accepted booking is findable by phone through the real endpoint.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Characterization test for a SECOND, pre-existing defect that guest identity does not cause
    /// and cannot fix.
    ///
    /// <c>BookingReadRepository.ToProjection</c> only emits a <c>ManageBookingCandidate</c> when the
    /// canonical payload yields <c>booking_reference</c> AND <c>business_name</c>
    /// (<c>BookingReadRepository.cs:120-124</c>). Neither is a field of <c>BookingRequest</c>:
    ///
    ///   • <c>booking_reference</c> HAS an authoritative source — <c>Booking.BookingReference</c>,
    ///     written at finalize — but <c>BaseRows</c> joins only <c>booking_operations</c> ×
    ///     <c>booking_intent_revisions</c>, never <c>bookings</c>.
    ///   • <c>business_name</c> has NO source anywhere in the platform. There is no tenant/business
    ///     profile entity, and <c>TenantContextView</c> (spec 03) carries no display name. Supplying
    ///     it needs a table spec 12 has not ratified, so this change does not invent one.
    ///
    /// The pre-existing suites hide this by hand-seeding a payload carrying <c>booking_id</c>,
    /// <c>business_name</c>, <c>booking_date</c> and <c>approx_time</c> — none of which the
    /// submission path can produce.
    ///
    /// This test asserts today's real behaviour so the gap is visible rather than silently green.
    /// When a business-name source lands, this test SHOULD fail — flip it to the assertion in
    /// <see cref="GetBookingsByPhone_FindsABookingSubmittedThroughTheRealFrontDoor"/>.
    /// </summary>
    [Fact]
    public async Task GetBookingsByPhone_CandidateRenderingIsBlockedOnAMissingBusinessNameSource()
    {
        var tenantId = $"guest-g2b-{Guid.NewGuid():N}";
        const string phone = "+61400999000";
        await SubmitWithGuestAsync(tenantId, phone);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/bookings?phone_e164={Uri.EscapeDataString(phone)}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal("none", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetBookingsByPhone_FindsABookingSubmittedThroughTheRealFrontDoor()
    {
        var tenantId = $"guest-g2-{Guid.NewGuid():N}";
        const string phone = "+61400333444";
        var operationId = await SubmitWithGuestAsync(tenantId, phone);

        // Advance the operation to the state a finalized booking reaches, so this test is
        // blocked only by the projection contract and not by "not booked yet".
        await using (var seed = fixture.CreateContext(tenantId))
        {
            var op = await seed.BookingOperations
                .IgnoreQueryFilters()
                .FirstAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
            op.BookingId = $"bk-{Guid.NewGuid():N}";
            op.Status = Cordia.Domain.Enums.OperationStatus.Booked;
            // business_name comes from the tenant's own display name and nothing else — it is
            // never derived from the tenant id, which would show a guest a slug.
            seed.TenantDisplayNames.Add(new Cordia.Domain.Entities.TenantDisplayName
            {
                TenantId = tenantId,
                DisplayName = "Serenity Spa",
            });
            seed.Bookings.Add(new Cordia.Domain.Entities.Booking
            {
                TenantId = tenantId,
                BookingId = op.BookingId,
                BookingReference = "CORD-G2",
                ProviderReference = $"prov-{op.BookingId}",
                Version = 1,
                LifecycleStatus = Cordia.Domain.Enums.BookingLifecycleStatus.Confirmed,
                CustomerId = phone,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/bookings?phone_e164={Uri.EscapeDataString(phone)}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsTextAsync();
        using var doc = JsonDocument.Parse(body);
        var candidates = doc.RootElement.GetProperty("candidates");

        Assert.True(
            candidates.GetArrayLength() > 0,
            $"GET /api/bookings?phone_e164= found nothing for a booking submitted through POST /api/bookings. Body: {body}");
        Assert.Equal("found", doc.RootElement.GetProperty("status").GetString());
    }

    // -------------------------------------------------------------------------
    // G3 — no sanitiser mutates the canonical payload on the way to storage.
    //
    // Trap: pre-persist redaction masks Luhn-valid digit runs, and a phone number is a digit
    // run. If the stored payload were sanitised it would stop matching payload_hash and every
    // replay would 422. This drives a real submission and re-hashes what actually landed in
    // the jsonb column rather than reasoning about the interceptor's scope.
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("+61400555666")]
    [InlineData("+14155552671")]
    // A Luhn-VALID 16-digit run: the shape the PAN detector exists to catch.
    [InlineData("+4539578000000808")]
    public async Task StoredCanonicalPayload_StillHashesToPayloadHash(string phone)
    {
        var tenantId = $"guest-g3-{Guid.NewGuid():N}";
        var operationId = await SubmitWithGuestAsync(tenantId, phone, serviceId: $"svc-{Guid.NewGuid():N}"[..12]);

        await using var ctx = fixture.CreateContext(tenantId);

        var operation = await ctx.BookingOperations
            .IgnoreQueryFilters()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == operationId);

        var revision = await ctx.BookingIntentRevisions
            .IgnoreQueryFilters()
            .FirstAsync(r => r.TenantId == tenantId && r.IntentId == operation.IntentId);

        var rehashed = new CanonicalRequestHasher()
            .Hash(JsonNode.Parse(revision.CanonicalPayload))
            .Value;

        Assert.Equal(revision.PayloadHash, rehashed);
        Assert.Contains(phone, revision.CanonicalPayload);
    }

    // -------------------------------------------------------------------------
    // G5 — the guest participates in idempotency correctly (Inv-2 / Inv-3).
    //
    // The guest is part of the canonical hash, so it must behave like every other material
    // field: a duplicate delivery of the SAME booking replays, and the same key bound to a
    // DIFFERENT guest is a conflict rather than a silent overwrite of who the booking is for.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task DuplicateDeliveryOfTheSameGuestBookingReplaysRatherThanBookingTwice()
    {
        var tenantId = $"guest-g5a-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var (body, _) = BuildRequestWithGuest(
            operationId, idempotencyKey, intentId, "svc-dup", "+61400222333", "cust-dup");

        async Task<string> PostAsync()
        {
            var response = await fixture.Host.Scenario(x =>
            {
                x.Post.RawJson(body).ToUrl("/api/bookings");
                x.WithRequestHeader("X-Tenant-Id", tenantId);
                x.StatusCodeShouldBe(HttpStatusCode.Accepted);
            });
            using var doc = JsonDocument.Parse(await response.ReadAsTextAsync());
            return doc.RootElement.GetProperty("operation_id").GetString()!;
        }

        var first = await PostAsync();
        var second = await PostAsync();

        Assert.Equal(first, second);

        await using var ctx = fixture.CreateContext(tenantId);
        var operations = await ctx.BookingOperations
            .IgnoreQueryFilters()
            .Where(o => o.TenantId == tenantId)
            .ToListAsync();

        // One logical booking, not two — the redelivery replayed the acceptance.
        Assert.Single(operations);
    }

    [Fact]
    public async Task SameKeyBoundToADifferentGuestIsAConflict()
    {
        var tenantId = $"guest-g5b-{Guid.NewGuid():N}";
        var idempotencyKey = $"ik-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";

        var (first, _) = BuildRequestWithGuest(
            $"op-{Guid.NewGuid():N}", idempotencyKey, intentId, "svc-conflict", "+61400444555", "cust-1");

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(first).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // Identical in every respect EXCEPT the guest. If the guest were not material, this
        // would replay the first booking and quietly attach it to the wrong person.
        var (second, _) = BuildRequestWithGuest(
            $"op-{Guid.NewGuid():N}", idempotencyKey, intentId, "svc-conflict", "+61400666777", "cust-1");

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(second).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Conflict);
        });
    }

    /// <summary>
    /// A tenant that has not set a display name gets NO candidates, and that is deliberate.
    ///
    /// `business_name` is shown to a guest (spec 08 §4). The alternatives were to derive it from
    /// the tenant id — which would show a guest a slug like `guest-g6-3f9a…` — or to relax the
    /// candidate contract. Withholding the candidate is the only option that neither lies nor
    /// changes a ratified contract, so the venue name is a real setup step.
    /// </summary>
    [Fact]
    public async Task WithoutATenantDisplayNameNoCandidateIsProjected()
    {
        var tenantId = $"guest-g6-{Guid.NewGuid():N}";
        const string phone = "+61400121314";
        var operationId = await SubmitWithGuestAsync(tenantId, phone);

        await using (var seed = fixture.CreateContext(tenantId))
        {
            var op = await seed.BookingOperations
                .IgnoreQueryFilters()
                .FirstAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
            op.BookingId = $"bk-{Guid.NewGuid():N}";
            op.Status = Cordia.Domain.Enums.OperationStatus.Booked;
            seed.Bookings.Add(new Cordia.Domain.Entities.Booking
            {
                TenantId = tenantId,
                BookingId = op.BookingId,
                BookingReference = "CORD-G6",
                ProviderReference = $"prov-{op.BookingId}",
                Version = 1,
                LifecycleStatus = Cordia.Domain.Enums.BookingLifecycleStatus.Confirmed,
                CustomerId = phone,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            // Deliberately NO TenantDisplayNames row.
            await seed.SaveChangesAsync();
        }

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/bookings?phone_e164={Uri.EscapeDataString(phone)}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal("none", doc.RootElement.GetProperty("status").GetString());
    }

    /// <summary>Inv-13: one tenant's venue name must never appear on another tenant's booking.</summary>
    [Fact]
    public async Task ATenantDisplayNameDoesNotLeakAcrossTenants()
    {
        var tenantA = $"guest-g7a-{Guid.NewGuid():N}";
        var tenantB = $"guest-g7b-{Guid.NewGuid():N}";

        await using (var seed = fixture.CreateContext(tenantA))
        {
            seed.TenantDisplayNames.Add(new Cordia.Domain.Entities.TenantDisplayName
            {
                TenantId = tenantA,
                DisplayName = "Tenant A Spa",
            });
            await seed.SaveChangesAsync();
        }

        await using var ctx = fixture.CreateContext(tenantB);
        var leaked = await ctx.TenantDisplayNames.ToListAsync();

        // The global query filter is the guard; asserting through the scoped context proves it
        // rather than trusting that it is configured.
        Assert.DoesNotContain(leaked, t => t.DisplayName == "Tenant A Spa");
    }

    // -------------------------------------------------------------------------
    // G4 — a guest phone does not leak across tenants (Inv-13).
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetBookingsByPhone_DoesNotReturnAnotherTenantsGuest()
    {
        var tenantA = $"guest-g4a-{Guid.NewGuid():N}";
        var tenantB = $"guest-g4b-{Guid.NewGuid():N}";
        const string phone = "+61400777888";

        await SubmitWithGuestAsync(tenantA, phone);

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/bookings?phone_e164={Uri.EscapeDataString(phone)}");
            x.WithRequestHeader("X-Tenant-Id", tenantB);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = await result.ReadAsTextAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(0, doc.RootElement.GetProperty("candidates").GetArrayLength());
    }
}
