#pragma warning disable xUnit1051
using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class WaitlistPersistenceTests(PostgresFixture fixture)
{
    private static WaitlistEnrollment MakeEnrollment(string tenantId, string enrollmentId)
        => new()
        {
            TenantId = tenantId,
            EnrollmentId = enrollmentId,
            CustomerRef = "cust-waitlist-1",
            AcceptableFrom = DateTimeOffset.UtcNow.AddDays(1),
            AcceptableTo = DateTimeOffset.UtcNow.AddDays(7),
            PartySize = 2,
            LocationId = "loc-001",
            ServiceId = "svc-dinner",
            OfferLifetimeSeconds = 3600,
            DepositTerms = """{"amount_minor":2000,"currency":"AUD","reason":"hold"}""",
            NotificationChannel = "sms",
            AutoBook = """{"consent_ref":"cs-001","constraint_hash":"abc"}""",
            Version = 0,
        };

    // (1) Insert enrollment (with deposit_terms + auto_book jsonb), read back jsonb round-trip.
    [Fact]
    public async Task Insert_Enrollment_RoundTripsJsonb()
    {
        var tenantId = $"t5-wl-{Guid.NewGuid():N}";
        var enrollmentId = $"enr-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.WaitlistEnrollments.Add(MakeEnrollment(tenantId, enrollmentId));
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var loaded = await readCtx.WaitlistEnrollments.AsNoTracking()
            .SingleAsync(e => e.EnrollmentId == enrollmentId);

        Assert.Equal("cust-waitlist-1", loaded.CustomerRef);
        Assert.Equal(2, loaded.PartySize);

        // jsonb round-trip: PostgreSQL normalizes key order; compare via parsed JSON.
        var depositDoc = JsonDocument.Parse(loaded.DepositTerms!);
        Assert.Equal(2000, depositDoc.RootElement.GetProperty("amount_minor").GetInt32());
        Assert.Equal("AUD", depositDoc.RootElement.GetProperty("currency").GetString());
        Assert.Equal("hold", depositDoc.RootElement.GetProperty("reason").GetString());

        var autoBookDoc = JsonDocument.Parse(loaded.AutoBook!);
        Assert.Equal("cs-001", autoBookDoc.RootElement.GetProperty("consent_ref").GetString());
        Assert.Equal("abc", autoBookDoc.RootElement.GetProperty("constraint_hash").GetString());
    }

    // (2) Insert offer (FK → enrollment), version-token Offered → Expired exactly-one-wins.
    [Fact]
    public async Task WaitlistOffer_VersionToken_ExactlyOneWins()
    {
        var tenantId = $"t5-wloffer-{Guid.NewGuid():N}";
        var enrollmentId = $"enr-{Guid.NewGuid():N}";
        var offerId = $"off-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.WaitlistEnrollments.Add(MakeEnrollment(tenantId, enrollmentId));
            await ctx.SaveChangesAsync();
        }

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.WaitlistOffers.Add(new WaitlistOffer
            {
                TenantId = tenantId,
                OfferId = offerId,
                EnrollmentId = enrollmentId,
                Status = WaitlistOfferStatus.Offered,
                OfferExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                OfferedAt = DateTimeOffset.UtcNow,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx1 = fixture.CreateContext(tenantId);
        await using var ctx2 = fixture.CreateContext(tenantId);

        var rows1 = await VersionTokenUpdate.UpdateAsync<WaitlistOffer>(
            ctx1, expectedVersion: 0,
            e => e.TenantId == tenantId && e.OfferId == offerId,
            s => s.SetProperty(x => x.Status, WaitlistOfferStatus.Expired));

        var rows2 = await VersionTokenUpdate.UpdateAsync<WaitlistOffer>(
            ctx2, expectedVersion: 0,
            e => e.TenantId == tenantId && e.OfferId == offerId,
            s => s.SetProperty(x => x.Status, WaitlistOfferStatus.Expired));

        Assert.Equal(1, rows1 + rows2);

        await using var verifyCtx = fixture.CreateContext(tenantId);
        var reloaded = await verifyCtx.WaitlistOffers.AsNoTracking()
            .SingleAsync(o => o.OfferId == offerId);
        Assert.Equal(WaitlistOfferStatus.Expired, reloaded.Status);
        Assert.Equal(1L, reloaded.Version);
    }
}
