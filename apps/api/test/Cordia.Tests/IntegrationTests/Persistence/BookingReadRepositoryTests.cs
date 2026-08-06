#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Domain.Bookings;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class BookingReadRepositoryTests(PostgresFixture fixture)
{
    [Fact]
    public async Task FindManageBookingCandidatesAsync_IsTenantScoped()
    {
        var tenantA = $"t8-read-a-{Guid.NewGuid():N}";
        var tenantB = $"t8-read-b-{Guid.NewGuid():N}";
        var phone = "+61400000000";

        await using (var ctx = fixture.CreateContext(tenantA))
            await SeedBookedOperationAsync(ctx, tenantA, "bk-a", "CORD-A", "Serenity Spa", phone);

        await using (var ctx = fixture.CreateContext(tenantB))
            await SeedBookedOperationAsync(ctx, tenantB, "bk-b", "CORD-B", "Other Spa", phone);

        await using var readCtx = fixture.CreateContext(tenantA);
        var repo = new BookingReadRepository(readCtx, new TenantDisplayNameRepository(readCtx), new GuestContactRepository(readCtx));

        var candidates = await repo.FindManageBookingCandidatesAsync(
            tenantA,
            new ManageBookingDiscoveryHints(PhoneE164: phone));

        var candidate = Assert.Single(candidates);
        Assert.Equal("bk-a", candidate.BookingId);
        Assert.Equal("CORD-A", candidate.BookingReference);
        Assert.DoesNotContain(candidates, c => c.BookingId == "bk-b");
    }

    [Fact]
    public async Task GetBookingProjectionByBookingIdAsync_WrongTenant_ReturnsNull()
    {
        var tenantA = $"t8-detail-a-{Guid.NewGuid():N}";
        var tenantB = $"t8-detail-b-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantA))
            await SeedBookedOperationAsync(ctx, tenantA, "bk-owned-by-a", "CORD-A-DETAIL", "Serenity Spa", "+61400000001");

        await using var readCtx = fixture.CreateContext(tenantB);
        var repo = new BookingReadRepository(readCtx, new TenantDisplayNameRepository(readCtx), new GuestContactRepository(readCtx));

        var projection = await repo.GetBookingProjectionByBookingIdAsync(tenantB, "bk-owned-by-a");

        Assert.Null(projection);
    }

    [Fact]
    public async Task FindManageBookingCandidatesAsync_DoesNotLeakSensitiveFields()
    {
        var tenantId = $"t8-redact-{Guid.NewGuid():N}";
        var phone = "+61411111111";
        var sensitiveName = "Ada Sensitive";
        var sensitiveEmail = "ada@example.test";
        var sensitivePaymentToken = "pm_secret_123";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var payload = JsonSerializer.Serialize(new
            {
                booking_id = "bk-redact",
                booking_reference = "CORD-REDACT",
                business_name = "Serenity Spa",
                booking_date = "2026-07-10",
                approx_time = "19:00",
                party_size = 2,
                customer = new
                {
                    display_name = sensitiveName,
                    phone_e164 = phone,
                    email = sensitiveEmail,
                },
                contact_phone = phone,
                contact_email = sensitiveEmail,
                payment = new
                {
                    payment_token = sensitivePaymentToken,
                    card_last4 = "4242",
                },
            });

            await SeedBookedOperationAsync(ctx, tenantId, "bk-redact", "CORD-REDACT", "Serenity Spa", phone, payload);
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var repo = new BookingReadRepository(readCtx, new TenantDisplayNameRepository(readCtx), new GuestContactRepository(readCtx));

        var candidates = await repo.FindManageBookingCandidatesAsync(
            tenantId,
            new ManageBookingDiscoveryHints(PhoneE164: phone));

        var candidate = Assert.Single(candidates);
        var serializedCandidate = JsonSerializer.Serialize(candidate);

        Assert.Contains("CORD-REDACT", serializedCandidate);
        Assert.DoesNotContain(sensitiveName, serializedCandidate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(phone, serializedCandidate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sensitiveEmail, serializedCandidate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sensitivePaymentToken, serializedCandidate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("4242", serializedCandidate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("customer", serializedCandidate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contact", serializedCandidate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payment", serializedCandidate, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SeedBookedOperationAsync(
        ApplicationDbContext ctx,
        string tenantId,
        string bookingId,
        string bookingReference,
        string businessName,
        string phoneE164,
        string? canonicalPayload = null)
    {
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        // Shaped like a real BookingRequest wherever the projection allows it: `booking_id` comes
        // from the operation row, and `booking_date`/`approx_time` are derived from `starts_at`,
        // so none of the three belongs in a payload. `booking_reference` and `business_name` are
        // the two the platform still cannot supply — see the note in
        // ManageBookingReadEndpointTests.SeedManageableBookingAsync.
        canonicalPayload ??= JsonSerializer.Serialize(new
        {
            operation = "create",
            service_id = "svc-spa",
            starts_at = "2026-07-10T19:00:00+00:00",
            party_size = 2,
            customer = new
            {
                phone_e164 = phoneE164,
            },
            // --- not producible by the submission path ---
            booking_reference = bookingReference,
            business_name = businessName,
        });

        var intent = new BookingIntent
        {
            TenantId = tenantId,
            IntentId = intentId,
            OperationType = OperationTypeEnum.Create,
            CreatedAt = now,
        };

        var revision = new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = canonicalPayload,
            PayloadHash = $"h-{Guid.NewGuid():N}",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
            CreatedAt = now,
            Intent = intent,
        };

        var operation = new BookingOperation
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = OperationStatus.Booked,
            BookingId = bookingId,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 1,
        };

        ctx.BookingIntents.Add(intent);
        ctx.BookingIntentRevisions.Add(revision);
        ctx.BookingOperations.Add(operation);
        await ctx.SaveChangesAsync();
    }
}
