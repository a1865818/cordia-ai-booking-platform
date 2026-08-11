#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// The venue name's write path (docs §12 gap 11 / §13 B2).
///
/// A read-only table would have been a table the platform never fills: `BusinessNameAsync`
/// would always answer null, no `ManageBookingCandidate` would ever project, and
/// `GET /api/bookings?phone_e164=` would still find nothing outside the test suite — the exact
/// defect the table was added to fix. The last test here drives the whole loop through HTTP so
/// that cannot regress unnoticed.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class TenantDisplayNameTests(AcceptanceApiFixture fixture)
{
    private static void Owner(Alba.Scenario x, string tenantId)
    {
        x.WithRequestHeader("X-Tenant-Id", tenantId);
        x.WithRequestHeader("X-Actor", "owner@example.com");
        x.WithRequestHeader("X-Actor-Role", "owner");
        x.WithRequestHeader("X-Reason-Code", "venue setup");
    }

    private async Task PutAsync(string tenantId, string name, HttpStatusCode expected = HttpStatusCode.OK)
        => await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new { display_name = name }).ToUrl("/api/tenant-profile/display-name");
            Owner(x, tenantId);
            x.StatusCodeShouldBe(expected);
        });

    [Fact]
    public async Task SetsAndReadsBackTheVenueName()
    {
        var tenantId = TestIds.New("tdn-set");
        await PutAsync(tenantId, "Serenity Spa");

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/tenant-profile/display-name");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal("Serenity Spa", doc.RootElement.GetProperty("display_name").GetString());
    }

    [Fact]
    public async Task IsIdempotent_ASecondPutUpdatesRatherThanDuplicating()
    {
        var tenantId = TestIds.New("tdn-idem");
        await PutAsync(tenantId, "First Name");
        await PutAsync(tenantId, "Second Name");

        await using var ctx = fixture.CreateContext(tenantId);
        // The tenant IS the key, so a second row is impossible rather than merely discouraged.
        var rows = ctx.TenantDisplayNames.Where(t => t.TenantId == tenantId).ToList();
        Assert.Single(rows);
        Assert.Equal("Second Name", rows[0].DisplayName);
    }

    [Fact]
    public async Task TrimsTheNameSoAGuestNeverSeesPaddedCopy()
    {
        var tenantId = TestIds.New("tdn-trim");
        await PutAsync(tenantId, "   Serenity Spa   ");

        await using var ctx = fixture.CreateContext(tenantId);
        Assert.Equal("Serenity Spa", ctx.TenantDisplayNames.Single(t => t.TenantId == tenantId).DisplayName);
    }

    [Fact]
    public async Task RefusesAWhitespaceOnlyName()
    {
        // A blank venue name renders as a blank venue to a guest — worse than withholding the
        // candidate, because it looks like a successful read.
        await PutAsync(TestIds.New("tdn-blank"), "   ", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task NonOwnerCannotSetIt()
    {
        var tenantId = TestIds.New("tdn-role");

        await fixture.Host.Scenario(x =>
        {
            x.Put.Json(new { display_name = "Hijacked" }).ToUrl("/api/tenant-profile/display-name");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "staff@example.com");
            x.WithRequestHeader("X-Actor-Role", "operator");
            x.WithRequestHeader("X-Reason-Code", "trying it on");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task MissingTenantIs400()
    {
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/tenant-profile/display-name");
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task OneTenantsVenueNameIsNotVisibleToAnother()
    {
        var tenantA = TestIds.New("tdn-xta");
        var tenantB = TestIds.New("tdn-xtb");
        await PutAsync(tenantA, "Tenant A Spa");

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/tenant-profile/display-name");
            x.WithRequestHeader("X-Tenant-Id", tenantB);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal(string.Empty, doc.RootElement.GetProperty("display_name").GetString());
    }

    /// <summary>
    /// The whole loop, over HTTP only: set the venue name, submit a booking with a guest,
    /// finalize it, then find it by phone.
    ///
    /// Every other B2 test seeds the display name through the DbContext, which would keep
    /// passing if the write endpoint were deleted. This one would not.
    /// </summary>
    [Fact]
    public async Task TheVenueNameSetOverHttpIsWhatMakesAPhoneLookupReturnACandidate()
    {
        var tenantId = TestIds.New("tdn-loop");
        const string phone = "+61400778899";

        // 1. Before any venue name: the lookup finds nothing, however complete the booking is.
        var operationId = await SeedBookedBookingAsync(tenantId, phone);
        Assert.Equal("none", await LookupStatusAsync(tenantId, phone));

        // 2. Set it through the endpoint — no DbContext seeding.
        await PutAsync(tenantId, "Serenity Spa");

        // 3. The same booking is now discoverable.
        Assert.Equal("found", await LookupStatusAsync(tenantId, phone));
        Assert.NotNull(operationId);
    }

    private async Task<string> LookupStatusAsync(string tenantId, string phone)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/bookings?phone_e164={Uri.EscapeDataString(phone)}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBeOk();
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        return doc.RootElement.GetProperty("status").GetString()!;
    }

    /// <summary>Submits through the real front door, then advances it to the booked state.</summary>
    private async Task<string> SeedBookedBookingAsync(string tenantId, string phone)
    {
        var operationId = $"op-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var payloadJson = $$"""
        {
          "operation": "create",
          "service_id": "svc-loop",
          "location_id": "",
          "target_booking_id": "",
          "starts_at": "2026-09-01T09:00:00.000Z",
          "party_size": 2,
          "special_requests": [],
          "customer": { "phone_e164": "{{phone}}" }
        }
        """;
        var hash = new Cordia.Infrastructure.Services.CanonicalRequestHasher()
            .Hash(System.Text.Json.Nodes.JsonNode.Parse(payloadJson)).Value;

        var body = $$"""
        {
          "operation_id": "{{operationId}}",
          "idempotency_key": "ik-{{Guid.NewGuid():N}}",
          "intent_id": "{{intentId}}",
          "intent_revision": 1,
          "replaces_operation_id": "",
          "revision": {
            "intent_id": "{{intentId}}", "revision": 1,
            "canonical_payload": {{payloadJson}},
            "schema_version": "intent.v1", "tenant_policy_version": "policy.v1",
            "payload_hash": "{{hash}}", "created_at": "2026-07-01T00:00:00.000Z",
            "source_session_id": "sess-loop", "supersession_reason": ""
          },
          "confirmation": {
            "confirmation_id": "{{Guid.NewGuid()}}", "intent_id": "{{intentId}}",
            "intent_revision": 1, "intent_payload_hash": "{{hash}}",
            "identity": "operator:console", "confirmation_channel": "web",
            "confirmed_at": "2026-07-01T00:00:00.000Z", "source_turn_id": "turn-loop",
            "read_back_version": "rb.v1", "disclosures_presented": [],
            "verification_level": "unverified"
          }
        }
        """;

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl("/api/bookings");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.Accepted);
        });

        // Advance to the state a finalized booking reaches. There is no HTTP route that does
        // this — finalize runs in the background lane — so the booking row is written directly.
        await using var ctx = fixture.CreateContext(tenantId);
        var op = ctx.BookingOperations.Single(o => o.OperationId == operationId);
        op.BookingId = $"bk-{Guid.NewGuid():N}";
        op.Status = Cordia.Domain.Enums.OperationStatus.Booked;
        ctx.Bookings.Add(new Cordia.Domain.Entities.Booking
        {
            TenantId = tenantId,
            BookingId = op.BookingId,
            BookingReference = "CORD-LOOP",
            ProviderReference = $"prov-{op.BookingId}",
            Version = 1,
            LifecycleStatus = Cordia.Domain.Enums.BookingLifecycleStatus.Confirmed,
            CustomerId = phone,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();

        return operationId;
    }
}
