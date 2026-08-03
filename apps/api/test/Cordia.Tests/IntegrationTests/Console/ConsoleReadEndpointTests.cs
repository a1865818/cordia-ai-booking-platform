#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework;

namespace Cordia.Tests.IntegrationTests.Console;

/// <summary>
/// Integration tests for the six tenant-scoped console read endpoints.
///
/// Each test uses a fresh tenant id, and every endpoint gets an explicit **cross-tenant**
/// case: the rows of tenant B must be invisible to tenant A (Inv-13). That is the assertion
/// that actually matters here — these are new read surfaces over shared hot tables, and a
/// missing tenant predicate would leak another venue's bookings without any test noticing.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class ConsoleReadEndpointTests(Acceptance.AcceptanceApiFixture fixture)
{
    private static string NewTenant() => $"console-{Guid.NewGuid():N}";

    // ---------------------------------------------------------------------
    // Seeding helpers
    // ---------------------------------------------------------------------

    private static async Task<string> SeedBookingOperationAsync(
        ApplicationDbContext ctx,
        string tenantId,
        OperationStatus status,
        string canonicalPayload,
        OperationTypeEnum type = OperationTypeEnum.Create,
        DateTimeOffset? at = null,
        string? sessionId = null)
    {
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var now = at ?? DateTimeOffset.UtcNow;

        ctx.BookingIntents.Add(new BookingIntent
        {
            TenantId = tenantId,
            IntentId = intentId,
            OperationType = type,
        });
        ctx.BookingIntentRevisions.Add(new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = canonicalPayload,
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
            SourceSessionId = sessionId ?? string.Empty,
        });
        ctx.BookingOperations.Add(new BookingOperation
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = type,
            Status = status,
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await ctx.SaveChangesAsync();
        return operationId;
    }

    /// <summary>
    /// A canonical payload as the API would store it. Note it carries NO customer field:
    /// <c>BookingRequest</c> declares none, so customer identity has to come from the
    /// bookings row or the confirmation — which <see cref="SeedCustomerIdentityAsync"/> does.
    /// </summary>
    private static string PayloadFor(DateTimeOffset startsAt, string serviceId, string bookingId)
        => JsonSerializer.Serialize(new
        {
            operation = "create",
            booking_id = bookingId,
            service_id = serviceId,
            starts_at = startsAt.ToString("o"),
            party_size = 4,
            duration_minutes = 45,
        });


    /// <summary>
    /// The canonical payload a **create** actually stores — no <c>booking_id</c>, no
    /// <c>target_booking_id</c>. <c>BookingRequest</c> declares neither (it is
    /// <c>additionalProperties: false</c>), and the operation row's <c>BookingId</c> is not
    /// assigned until <c>FinalizeBookingCommandHandler</c> writes the provider reference.
    ///
    /// <see cref="PayloadFor"/> bakes in a <c>booking_id</c>, which is convenient but not
    /// representative: it made every schedule assertion exercise the already-finalised case only.
    /// </summary>
    private static string InFlightCreatePayload(DateTimeOffset startsAt, string serviceId)
        => JsonSerializer.Serialize(new
        {
            operation = "create",
            service_id = serviceId,
            starts_at = startsAt.ToString("o"),
            party_size = 4,
            duration_minutes = 45,
        });

    /// <summary>Materialises the authoritative booking row that carries the customer id.</summary>
    private static async Task SeedCustomerIdentityAsync(
        ApplicationDbContext ctx,
        string tenantId,
        string bookingId,
        string customerRef)
    {
        ctx.Bookings.Add(new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = bookingId,
            CustomerId = customerRef,
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
            Version = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<string> SeedSessionAsync(
        ApplicationDbContext ctx,
        string tenantId,
        TenantChannel channel,
        DateTimeOffset? startedAt = null)
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        ctx.ConversationSessions.Add(new ConversationSession
        {
            TenantId = tenantId,
            SessionId = sessionId,
            Channel = channel,
            StartedAt = startedAt ?? DateTimeOffset.UtcNow,
            Version = 0,
        });
        await ctx.SaveChangesAsync();
        return sessionId;
    }

    private static async Task<string> SeedEnrollmentAsync(
        ApplicationDbContext ctx,
        string tenantId,
        bool cancelled = false,
        string? autoBook = null)
    {
        var enrollmentId = $"wl-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        ctx.WaitlistEnrollments.Add(new WaitlistEnrollment
        {
            TenantId = tenantId,
            EnrollmentId = enrollmentId,
            CustomerRef = "cust-1",
            AcceptableFrom = now,
            AcceptableTo = now.AddDays(1),
            PartySize = 2,
            LocationId = "loc-1",
            ServiceId = "gel",
            OfferLifetimeSeconds = 600,
            NotificationChannel = "sms",
            AutoBook = autoBook,
            IdentityRef = "id-1",
            CreatedAt = now,
            CancelledAt = cancelled ? now : null,
            Version = 0,
        });
        await ctx.SaveChangesAsync();
        return enrollmentId;
    }

    private async Task<JsonElement> GetJsonAsync(string url, string tenantId)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url(url);
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        return JsonDocument.Parse(await result.ReadAsTextAsync()).RootElement.Clone();
    }

    private async Task AssertMissingTenantIsRejectedAsync(string url)
    {
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url(url);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ---------------------------------------------------------------------
    // GET /api/Bookings/schedule
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Schedule_ReturnsBookingsInsideTheWindowOnly()
    {
        var tenantId = NewTenant();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedBookingOperationAsync(
            ctx, tenantId, OperationStatus.Booked, PayloadFor(start, "gel", "bk-in"));
        await SeedBookingOperationAsync(
            ctx, tenantId, OperationStatus.Booked, PayloadFor(start.AddDays(30), "gel", "bk-out"));
        await SeedCustomerIdentityAsync(ctx, tenantId, "bk-in", "cust-a");

        var from = start.AddHours(-1);
        var to = start.AddHours(1);
        var root = await GetJsonAsync(
            $"/api/Bookings/schedule?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}",
            tenantId);

        var bookings = root.GetProperty("bookings").EnumerateArray().ToList();
        Assert.Single(bookings);
        Assert.Equal("bk-in", bookings[0].GetProperty("booking_id").GetString());
        Assert.Equal("gel", bookings[0].GetProperty("service_id").GetString());
        Assert.Equal("cust-a", bookings[0].GetProperty("customer_ref").GetString());
        Assert.Equal("confirmed", bookings[0].GetProperty("lifecycle_status").GetString());
        Assert.Equal("booked", bookings[0].GetProperty("operation_status").GetString());
        Assert.Equal(4, bookings[0].GetProperty("party_size").GetInt32());
    }


    [Theory]
    [InlineData(OperationStatus.Accepted)]
    [InlineData(OperationStatus.Validating)]
    [InlineData(OperationStatus.ReservingCapacity)]
    [InlineData(OperationStatus.Held)]
    [InlineData(OperationStatus.AwaitingPayment)]
    [InlineData(OperationStatus.Finalizing)]
    public async Task Schedule_ShowsBookingsThatAreStillInFlight(OperationStatus status)
    {
        // A booking that has been accepted but not yet finalised still OCCUPIES ITS SLOT. Dropping
        // it from the grid tells the operator the time is free, and a second operator books over
        // the top of a guest who already holds it.
        var tenantId = NewTenant();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        await using var ctx = fixture.CreateContext(tenantId);
        var operationId = await SeedBookingOperationAsync(
            ctx, tenantId, status, InFlightCreatePayload(start, "gel"));

        var root = await GetJsonAsync(
            $"/api/Bookings/schedule?from={Uri.EscapeDataString(start.AddHours(-1).ToString("o"))}&to={Uri.EscapeDataString(start.AddHours(1).ToString("o"))}",
            tenantId);

        var bookings = root.GetProperty("bookings").EnumerateArray().ToList();
        var row = Assert.Single(bookings);

        // The operation id is the only stable handle it has — there is no booking row yet.
        Assert.Equal(operationId, row.GetProperty("operation_id").GetString());
        Assert.Equal("gel", row.GetProperty("service_id").GetString());
        Assert.Equal(4, row.GetProperty("party_size").GetInt32());
    }

    [Fact]
    public async Task Schedule_DoesNotInventABookingIdForAnInFlightOperation()
    {
        // The row must not claim a booking that does not exist yet: acting on a fabricated id
        // (cancel/modify) would 404, and the console decides what is actionable from this field.
        var tenantId = NewTenant();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedBookingOperationAsync(
            ctx, tenantId, OperationStatus.Held, InFlightCreatePayload(start, "gel"));

        var root = await GetJsonAsync(
            $"/api/Bookings/schedule?from={Uri.EscapeDataString(start.AddHours(-1).ToString("o"))}&to={Uri.EscapeDataString(start.AddHours(1).ToString("o"))}",
            tenantId);

        var row = Assert.Single(root.GetProperty("bookings").EnumerateArray());
        var bookingId = row.GetProperty("booking_id");
        Assert.True(
            bookingId.ValueKind == JsonValueKind.Null || string.IsNullOrEmpty(bookingId.GetString()),
            $"expected no booking id for an unfinalised operation, got '{bookingId}'");
    }

    [Fact]
    public async Task Schedule_ReportsAnInFlightOperationAsNotYetConfirmed()
    {
        var tenantId = NewTenant();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedBookingOperationAsync(
            ctx, tenantId, OperationStatus.Held, InFlightCreatePayload(start, "gel"));

        var root = await GetJsonAsync(
            $"/api/Bookings/schedule?from={Uri.EscapeDataString(start.AddHours(-1).ToString("o"))}&to={Uri.EscapeDataString(start.AddHours(1).ToString("o"))}",
            tenantId);

        var row = Assert.Single(root.GetProperty("bookings").EnumerateArray());
        Assert.Equal("held", row.GetProperty("operation_status").GetString());
        // Not "confirmed": the platform has not said so yet (Inv-16).
        Assert.NotEqual("confirmed", row.GetProperty("lifecycle_status").GetString());
    }

    [Fact]
    public async Task Schedule_KeepsOneRowPerOperationOnceItFinalises()
    {
        // The dedupe key changes as the workflow progresses (operation id while in flight, booking
        // id afterwards). One operation must still produce exactly one row.
        var tenantId = NewTenant();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedBookingOperationAsync(
            ctx, tenantId, OperationStatus.Booked, PayloadFor(start, "gel", "bk-done"));
        await SeedCustomerIdentityAsync(ctx, tenantId, "bk-done", "cust-done");

        var root = await GetJsonAsync(
            $"/api/Bookings/schedule?from={Uri.EscapeDataString(start.AddHours(-1).ToString("o"))}&to={Uri.EscapeDataString(start.AddHours(1).ToString("o"))}",
            tenantId);

        var row = Assert.Single(root.GetProperty("bookings").EnumerateArray());
        Assert.Equal("bk-done", row.GetProperty("booking_id").GetString());
    }

    [Fact]
    public async Task Schedule_DoesNotLeakAnotherTenantsBookings()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var start = DateTimeOffset.UtcNow.AddDays(1);

        await using (var ctxB = fixture.CreateContext(tenantB))
        {
            await SeedBookingOperationAsync(
                ctxB, tenantB, OperationStatus.Booked, PayloadFor(start, "gel", "bk-b"));
        }

        var root = await GetJsonAsync(
            $"/api/Bookings/schedule?from={Uri.EscapeDataString(start.AddHours(-1).ToString("o"))}&to={Uri.EscapeDataString(start.AddHours(1).ToString("o"))}",
            tenantA);

        Assert.Empty(root.GetProperty("bookings").EnumerateArray());
    }

    [Fact]
    public async Task Schedule_RejectsMissingTenantAndBadRange()
    {
        await AssertMissingTenantIsRejectedAsync("/api/Bookings/schedule");

        var tenantId = NewTenant();
        var now = DateTimeOffset.UtcNow;
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url(
                $"/api/Bookings/schedule?from={Uri.EscapeDataString(now.ToString("o"))}&to={Uri.EscapeDataString(now.AddDays(-1).ToString("o"))}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/Bookings/schedule?limit=5000");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ---------------------------------------------------------------------
    // GET /api/booking-operations
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Operations_FilterByStatusAndNeedsAttention()
    {
        var tenantId = NewTenant();
        var payload = PayloadFor(DateTimeOffset.UtcNow, "gel", "bk-1");

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedBookingOperationAsync(ctx, tenantId, OperationStatus.Booked, payload);
        await SeedBookingOperationAsync(ctx, tenantId, OperationStatus.ManualReview, payload);

        var all = await GetJsonAsync("/api/booking-operations", tenantId);
        Assert.Equal(2, all.GetProperty("operations").GetArrayLength());

        var filtered = await GetJsonAsync("/api/booking-operations?status=manual_review", tenantId);
        var rows = filtered.GetProperty("operations").EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal("manual_review", rows[0].GetProperty("status").GetString());
        Assert.Equal("create", rows[0].GetProperty("operation_type").GetString());

        var attention = await GetJsonAsync("/api/booking-operations?needs_attention=true", tenantId);
        Assert.Single(attention.GetProperty("operations").EnumerateArray());
    }

    [Fact]
    public async Task Operations_RejectAnUnknownStatusRatherThanIgnoringIt()
    {
        var tenantId = NewTenant();

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/booking-operations?status=not_a_real_status");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    [Fact]
    public async Task Operations_DoNotLeakAnotherTenantsRows()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        await using (var ctxB = fixture.CreateContext(tenantB))
        {
            await SeedBookingOperationAsync(
                ctxB, tenantB, OperationStatus.ManualReview,
                PayloadFor(DateTimeOffset.UtcNow, "gel", "bk-b"));
        }

        var root = await GetJsonAsync("/api/booking-operations", tenantA);
        Assert.Empty(root.GetProperty("operations").EnumerateArray());
    }

    // ---------------------------------------------------------------------
    // GET /api/conversation-sessions
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Sessions_ReturnMetadataAndNeverTranscriptText()
    {
        var tenantId = NewTenant();

        await using var ctx = fixture.CreateContext(tenantId);
        var sessionId = await SeedSessionAsync(ctx, tenantId, TenantChannel.Web);

        var root = await GetJsonAsync("/api/conversation-sessions", tenantId);
        var sessions = root.GetProperty("sessions").EnumerateArray().ToList();

        Assert.Single(sessions);
        Assert.Equal(sessionId, sessions[0].GetProperty("session_id").GetString());
        Assert.Equal("web", sessions[0].GetProperty("channel").GetString());
        Assert.False(sessions[0].TryGetProperty("turns", out _));
        Assert.False(sessions[0].TryGetProperty("text", out _));
    }

    [Fact]
    public async Task Sessions_DoNotLeakAnotherTenantsRows()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        await using (var ctxB = fixture.CreateContext(tenantB))
        {
            await SeedSessionAsync(ctxB, tenantB, TenantChannel.Voice);
        }

        var root = await GetJsonAsync("/api/conversation-sessions", tenantA);
        Assert.Empty(root.GetProperty("sessions").EnumerateArray());
    }

    // ---------------------------------------------------------------------
    // GET /api/waitlist/enrollments
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Waitlist_ExcludesCancelledByDefaultAndReportsAutoBookGrant()
    {
        var tenantId = NewTenant();

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedEnrollmentAsync(ctx, tenantId, autoBook: "{\"consent_evidence_id\":\"e1\"}");
        await SeedEnrollmentAsync(ctx, tenantId, cancelled: true);

        var active = await GetJsonAsync("/api/waitlist/enrollments", tenantId);
        var rows = active.GetProperty("enrollments").EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.True(rows[0].GetProperty("auto_book_authorized").GetBoolean());
        Assert.False(rows[0].GetProperty("cancelled").GetBoolean());

        var all = await GetJsonAsync("/api/waitlist/enrollments?active_only=false", tenantId);
        Assert.Equal(2, all.GetProperty("enrollments").GetArrayLength());
    }

    [Fact]
    public async Task Waitlist_DoesNotLeakAnotherTenantsEnrollments()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        await using (var ctxB = fixture.CreateContext(tenantB))
        {
            await SeedEnrollmentAsync(ctxB, tenantB);
        }

        var root = await GetJsonAsync("/api/waitlist/enrollments", tenantA);
        Assert.Empty(root.GetProperty("enrollments").EnumerateArray());
    }

    // ---------------------------------------------------------------------
    // GET /api/customers
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Customers_AggregateBookingHistoryWithoutExposingContactDetails()
    {
        var tenantId = NewTenant();
        var now = DateTimeOffset.UtcNow;

        await using var ctx = fixture.CreateContext(tenantId);
        await SeedBookingOperationAsync(
            ctx, tenantId, OperationStatus.Booked, PayloadFor(now, "gel", "bk-1"));
        await SeedBookingOperationAsync(
            ctx, tenantId, OperationStatus.Booked, PayloadFor(now.AddDays(1), "sns", "bk-2"));
        await SeedBookingOperationAsync(
            ctx, tenantId, OperationStatus.Booked, PayloadFor(now, "gel", "bk-3"));
        // Customer identity lives on the bookings row, never on the canonical payload.
        await SeedCustomerIdentityAsync(ctx, tenantId, "bk-1", "cust-a");
        await SeedCustomerIdentityAsync(ctx, tenantId, "bk-2", "cust-a");
        await SeedCustomerIdentityAsync(ctx, tenantId, "bk-3", "cust-b");

        var root = await GetJsonAsync("/api/customers", tenantId);
        var customers = root.GetProperty("customers").EnumerateArray().ToList();

        Assert.Equal(2, customers.Count);
        var a = customers.Single(c => c.GetProperty("customer_ref").GetString() == "cust-a");
        Assert.Equal(2, a.GetProperty("booking_count").GetInt32());
        // Minimisation: no name, phone or email is ever projected here.
        Assert.False(a.TryGetProperty("phone", out _));
        Assert.False(a.TryGetProperty("email", out _));
        Assert.False(a.TryGetProperty("name", out _));

        var filtered = await GetJsonAsync("/api/customers?query=cust-b", tenantId);
        Assert.Single(filtered.GetProperty("customers").EnumerateArray());
    }

    [Fact]
    public async Task Customers_DoNotLeakAnotherTenantsHistory()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();

        await using (var ctxB = fixture.CreateContext(tenantB))
        {
            await SeedBookingOperationAsync(
                ctxB, tenantB, OperationStatus.Booked,
                PayloadFor(DateTimeOffset.UtcNow, "gel", "bk-b"));
            await SeedCustomerIdentityAsync(ctxB, tenantB, "bk-b", "cust-b");
        }

        // Tenant B genuinely has a customer, so an empty result for A proves scoping
        // rather than proving the projection is simply always empty.
        var forB = await GetJsonAsync("/api/customers", tenantB);
        Assert.Single(forB.GetProperty("customers").EnumerateArray());

        var root = await GetJsonAsync("/api/customers", tenantA);
        Assert.Empty(root.GetProperty("customers").EnumerateArray());
    }

    // ---------------------------------------------------------------------
    // GET /api/reporting/overview
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Overview_AggregatesPerDayChannelAndService()
    {
        var tenantId = NewTenant();
        var now = DateTimeOffset.UtcNow;

        await using var ctx = fixture.CreateContext(tenantId);
        var sessionId = await SeedSessionAsync(ctx, tenantId, TenantChannel.Web);
        await SeedBookingOperationAsync(
            ctx, tenantId, OperationStatus.Booked,
            PayloadFor(now, "gel", "bk-1"), at: now, sessionId: sessionId);
        await SeedBookingOperationAsync(
            ctx, tenantId, OperationStatus.Accepted,
            PayloadFor(now, "gel", "bk-2"), at: now, sessionId: sessionId);

        var from = now.AddHours(-1);
        var to = now.AddHours(1);
        var root = await GetJsonAsync(
            $"/api/reporting/overview?from={Uri.EscapeDataString(from.ToString("o"))}&to={Uri.EscapeDataString(to.ToString("o"))}",
            tenantId);

        var perDay = root.GetProperty("per_day").EnumerateArray().ToList();
        Assert.Single(perDay);
        Assert.Equal(2, perDay[0].GetProperty("accepted").GetInt32());
        Assert.Equal(1, perDay[0].GetProperty("booked").GetInt32());

        var channels = root.GetProperty("channels").EnumerateArray().ToList();
        Assert.Single(channels);
        Assert.Equal("web", channels[0].GetProperty("channel").GetString());
        Assert.Equal(2, channels[0].GetProperty("count").GetInt32());

        var services = root.GetProperty("top_services").EnumerateArray().ToList();
        Assert.Single(services);
        Assert.Equal("gel", services[0].GetProperty("service_id").GetString());
        Assert.Equal(2, services[0].GetProperty("count").GetInt32());

        // Inv-16: no invented "who handled it" split.
        Assert.False(root.TryGetProperty("handled", out _));
    }

    [Fact]
    public async Task Overview_DoesNotLeakAnotherTenantsCounts()
    {
        var tenantA = NewTenant();
        var tenantB = NewTenant();
        var now = DateTimeOffset.UtcNow;

        await using (var ctxB = fixture.CreateContext(tenantB))
        {
            await SeedBookingOperationAsync(
                ctxB, tenantB, OperationStatus.Booked,
                PayloadFor(now, "gel", "bk-b"), at: now);
        }

        var root = await GetJsonAsync(
            $"/api/reporting/overview?from={Uri.EscapeDataString(now.AddHours(-1).ToString("o"))}&to={Uri.EscapeDataString(now.AddHours(1).ToString("o"))}",
            tenantA);

        Assert.Empty(root.GetProperty("per_day").EnumerateArray());
        Assert.Empty(root.GetProperty("channels").EnumerateArray());
        Assert.Empty(root.GetProperty("top_services").EnumerateArray());
    }

    [Fact]
    public async Task Overview_RejectsMissingTenantAndInvertedWindow()
    {
        await AssertMissingTenantIsRejectedAsync("/api/reporting/overview");

        var tenantId = NewTenant();
        var now = DateTimeOffset.UtcNow;
        await fixture.Host.Scenario(x =>
        {
            x.Get.Url(
                $"/api/reporting/overview?from={Uri.EscapeDataString(now.ToString("o"))}&to={Uri.EscapeDataString(now.AddDays(-1).ToString("o"))}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // ---------------------------------------------------------------------
    // Tenant guard on the remaining endpoints
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("/api/booking-operations")]
    [InlineData("/api/conversation-sessions")]
    [InlineData("/api/waitlist/enrollments")]
    [InlineData("/api/customers")]
    public async Task EveryConsoleReadRequiresATenant(string url)
        => await AssertMissingTenantIsRejectedAsync(url);
}
