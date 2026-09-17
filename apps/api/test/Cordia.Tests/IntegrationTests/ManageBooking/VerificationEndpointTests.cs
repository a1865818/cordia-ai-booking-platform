#pragma warning disable xUnit1051
using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Cordia.Tests.IntegrationTests.ManageBooking;

[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class VerificationEndpointTests(AcceptanceApiFixture fixture)
{
    [Fact]
    public async Task RequestVerification_CreatesChallengeAndDeduplicatesByKey()
    {
        var tenantId = $"t8-ver-req-{Guid.NewGuid():N}";
        var body = RequestBody("idem-req-1", "cust-req", "bk-req", "modify");
        await SeedBookingAsync(tenantId, "bk-req", "cust-req");

        var first = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl("/api/verifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        var second = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(body).ToUrl("/api/verifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var firstDoc = JsonDocument.Parse(await first.ReadAsTextAsync());
        using var secondDoc = JsonDocument.Parse(await second.ReadAsTextAsync());

        Assert.Equal("sent", firstDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("already_sent", secondDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            firstDoc.RootElement.GetProperty("verification").GetProperty("verification_id").GetString(),
            secondDoc.RootElement.GetProperty("verification").GetProperty("verification_id").GetString());
    }

    [Fact]
    public async Task CheckVerification_PendingBadCodeThenGranted_IssuesGrantAndAudit()
    {
        var tenantId = $"t8-ver-check-{Guid.NewGuid():N}";
        await SeedBookingAsync(tenantId, "bk-check", "cust-check");
        var request = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(RequestBody("idem-check-1", "cust-check", "bk-check", "cancel"))
                .ToUrl("/api/verifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        using var requestDoc = JsonDocument.Parse(await request.ReadAsTextAsync());
        var verificationId = requestDoc.RootElement.GetProperty("verification").GetProperty("verification_id").GetString()!;
        // The test host exposes the generated code, because nothing delivers it yet (#71).
        var code = requestDoc.RootElement.GetProperty("development_code").GetString()!;

        var pending = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/verifications/{verificationId}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        using (var pendingDoc = JsonDocument.Parse(await pending.ReadAsTextAsync()))
            Assert.Equal("pending", pendingDoc.RootElement.GetProperty("status").GetString());

        var bad = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson($$"""{"code":"{{WrongCode(code)}}"}""").ToUrl($"/api/verifications/{verificationId}/verify");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        using (var badDoc = JsonDocument.Parse(await bad.ReadAsTextAsync()))
            Assert.Equal("failed", badDoc.RootElement.GetProperty("status").GetString());

        var granted = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson($$"""{"code":"{{code}}"}""").ToUrl($"/api/verifications/{verificationId}/verify");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Correlation-Id", "corr-verification-test");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        using var grantedDoc = JsonDocument.Parse(await granted.ReadAsTextAsync());
        Assert.Equal("granted", grantedDoc.RootElement.GetProperty("status").GetString());
        var grant = grantedDoc.RootElement.GetProperty("grant");
        Assert.Equal("cust-check", grant.GetProperty("customer_id").GetString());
        Assert.Equal("bk-check", grant.GetProperty("target_booking_id").GetString());
        Assert.Equal("cancel", grant.GetProperty("allowed_action").GetString());

        await using var ctx = fixture.CreateContext(tenantId);
        var grantId = grant.GetProperty("grant_id").GetString();
        var grantRow = await ctx.VerificationGrants.AsNoTracking()
            .SingleOrDefaultAsync(g => g.TenantId == tenantId && g.GrantId == grantId);
        Assert.NotNull(grantRow);
        Assert.Equal(VerificationActionEnum.Cancel, grantRow.AllowedAction);

        var audit = await ctx.AuditEvents.AsNoTracking()
            .SingleOrDefaultAsync(a => a.TenantId == tenantId && a.EventType == "grant_issued");
        Assert.NotNull(audit);
        Assert.Equal("corr-verification-test", audit.CorrelationId);
    }

    // ── #71: real codes and a lockout ──────────────────────────────────────────────────────────

    /// <summary>A six-digit code guaranteed to differ from <paramref name="code"/>.</summary>
    private static string WrongCode(string code)
        => ((int.Parse(code) + 1) % 1_000_000).ToString("D6");

    private async Task<(string VerificationId, string Code)> RequestChallengeAsync(string tenantId, string key)
    {
        await SeedBookingAsync(tenantId, $"bk-{key}", $"cust-{key}");
        var response = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson(RequestBody(key, $"cust-{key}", $"bk-{key}", "cancel")).ToUrl("/api/verifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        using var doc = JsonDocument.Parse(await response.ReadAsTextAsync());
        return (
            doc.RootElement.GetProperty("verification").GetProperty("verification_id").GetString()!,
            doc.RootElement.GetProperty("development_code").GetString()!);
    }

    private async Task<string> VerifyAsync(string tenantId, string verificationId, string code)
    {
        var response = await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson($$"""{"code":"{{code}}"}""").ToUrl($"/api/verifications/{verificationId}/verify");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        using var doc = JsonDocument.Parse(await response.ReadAsTextAsync());
        return doc.RootElement.GetProperty("status").GetString()!;
    }

    /// <summary>
    /// Every challenge used to be hashed under the fixed code "000000", so anyone who knew a
    /// verification id could verify it. Codes are now random per challenge.
    /// </summary>
    [Fact]
    public async Task EachChallenge_GetsItsOwnRandomCode()
    {
        var tenantId = $"t8-ver-rand-{Guid.NewGuid():N}";
        var codes = new List<string>();
        for (var i = 0; i < 4; i++)
            codes.Add((await RequestChallengeAsync(tenantId, $"rand-{i}-{Guid.NewGuid():N}")).Code);

        Assert.All(codes, c => Assert.Matches("^[0-9]{6}$", c));
        // Four independent draws from a million colliding into one value is ~1e-18 — this fails only
        // if the code is not random at all.
        Assert.True(codes.Distinct().Count() > 1, $"Every challenge got the same code: {codes[0]}");
    }

    [Fact]
    public async Task FiveWrongCodes_LockTheChallenge_SoEvenTheRightCodeFails()
    {
        var tenantId = $"t8-ver-lock-{Guid.NewGuid():N}";
        var (verificationId, code) = await RequestChallengeAsync(tenantId, $"lock-{Guid.NewGuid():N}");

        for (var i = 0; i < 5; i++)
            Assert.Equal("failed", await VerifyAsync(tenantId, verificationId, WrongCode(code)));

        Assert.Equal("failed", await VerifyAsync(tenantId, verificationId, code));

        // A poller must not be told to keep waiting on a challenge that can never grant.
        var check = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/verifications/{verificationId}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        using var checkDoc = JsonDocument.Parse(await check.ReadAsTextAsync());
        Assert.Equal("failed", checkDoc.RootElement.GetProperty("status").GetString());

        await using var ctx = fixture.CreateContext(tenantId);
        Assert.False(await ctx.VerificationGrants.AsNoTracking().AnyAsync(g => g.TenantId == tenantId));
        var row = await ctx.VerificationChallenges.AsNoTracking()
            .SingleAsync(c => c.TenantId == tenantId && c.VerificationId == verificationId);
        Assert.Equal(5, row.FailedAttempts);
        Assert.Null(row.VerifiedAt);
    }

    /// <summary>
    /// A challenge that was locked and has since expired reports the same terminal status on both
    /// endpoints. GET used to check the lock first and answer "failed" while POST /verify checked
    /// expiry first and answered "expired".
    /// </summary>
    [Fact]
    public async Task ALockedChallengeThatHasExpired_ReadsExpired_OnBothEndpoints()
    {
        var tenantId = $"t8-ver-lockexp-{Guid.NewGuid():N}";
        var (verificationId, code) = await RequestChallengeAsync(tenantId, $"lockexp-{Guid.NewGuid():N}");
        for (var i = 0; i < 5; i++)
            await VerifyAsync(tenantId, verificationId, WrongCode(code));

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            await ctx.VerificationChallenges
                .Where(c => c.TenantId == tenantId && c.VerificationId == verificationId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        var check = await fixture.Host.Scenario(x =>
        {
            x.Get.Url($"/api/verifications/{verificationId}");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
        using var checkDoc = JsonDocument.Parse(await check.ReadAsTextAsync());

        Assert.Equal("expired", checkDoc.RootElement.GetProperty("status").GetString());
        Assert.Equal("expired", await VerifyAsync(tenantId, verificationId, code));
    }

    /// <summary>The paired negative: wrong guesses below the limit do not cost the guest their code.</summary>
    [Fact]
    public async Task FourWrongCodes_StillLeaveTheRightCodeWorking()
    {
        var tenantId = $"t8-ver-four-{Guid.NewGuid():N}";
        var (verificationId, code) = await RequestChallengeAsync(tenantId, $"four-{Guid.NewGuid():N}");

        for (var i = 0; i < 4; i++)
            Assert.Equal("failed", await VerifyAsync(tenantId, verificationId, WrongCode(code)));

        Assert.Equal("granted", await VerifyAsync(tenantId, verificationId, code));
    }

    /// <summary>Outside Development and the test host the code is never returned to the caller.</summary>
    [Fact]
    public async Task TheCode_IsNotReturned_WhereExposureIsOff()
    {
        var tenantId = $"t8-ver-hidden-{Guid.NewGuid():N}";
        await SeedBookingAsync(tenantId, "bk-hidden", "cust-hidden");

        await using var host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:CordiaDBConnection", AcceptanceApiFixture.TestConnStr);
            builder.UseSetting("Verification:ExposeCodeForDevelopment", "false");
        });

        var response = await host.Scenario(x =>
        {
            x.Post.RawJson(RequestBody("idem-hidden", "cust-hidden", "bk-hidden", "cancel")).ToUrl("/api/verifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await response.ReadAsTextAsync());
        Assert.Equal("sent", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.TryGetProperty("development_code", out _));
    }

    /// <summary>
    /// The security-relevant DEFAULT: outside Development, with nobody setting
    /// <c>Verification:ExposeCodeForDevelopment</c>, the code is not returned. The test above only
    /// proves the explicit opt-out, because the "Test" environment loads a settings file that turns
    /// exposure on — so a regression that inverted the default outside Development would pass it.
    ///
    /// <para>This host runs as "Staging", which loads no environment settings file. It is given
    /// every test-host setting it needs to start (connection, committed test keys, the local pepper,
    /// the stub extractor, the tenant shim) EXCEPT the exposure flag, so the value it resolves is
    /// <c>Program.cs</c>'s own environment-derived default.</para>
    /// </summary>
    [Fact]
    public async Task OutsideDevelopment_TheCodeIsNotReturned_ByDefault()
    {
        var tenantId = $"t8-ver-default-{Guid.NewGuid():N}";
        await SeedBookingAsync(tenantId, "bk-default", "cust-default");

        var testSettings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Test.json"), optional: false)
            .Build()
            .AsEnumerable()
            .Where(kv => kv.Value is not null
                && !kv.Key.EndsWith("_comment", StringComparison.Ordinal)
                && !string.Equals(kv.Key, "Verification:ExposeCodeForDevelopment", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.DoesNotContain(testSettings, kv => kv.Key.Contains("ExposeCodeForDevelopment", StringComparison.OrdinalIgnoreCase));

        await using var host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Staging");
            foreach (var (key, value) in testSettings)
                builder.UseSetting(key, value);
            builder.UseSetting("ConnectionStrings:CordiaDBConnection", AcceptanceApiFixture.TestConnStr);
        });

        var response = await host.Scenario(x =>
        {
            x.Post.RawJson(RequestBody("idem-default", "cust-default", "bk-default", "cancel")).ToUrl("/api/verifications");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await response.ReadAsTextAsync());
        Assert.Equal("sent", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.TryGetProperty("development_code", out _));
    }

    private static string RequestBody(
        string idempotencyKey,
        string customerId,
        string bookingId,
        string action)
        => $$"""
        {
          "target_booking_id": "{{bookingId}}",
          "customer_id": "{{customerId}}",
          "channel": "sms",
          "action": "{{action}}",
          "verification_idempotency_key": "{{idempotencyKey}}"
        }
        """;

    private async Task SeedBookingAsync(string tenantId, string bookingId, string customerId)
    {
        var now = DateTimeOffset.UtcNow;
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.Bookings.Add(new Booking
        {
            TenantId = tenantId,
            BookingId = bookingId,
            BookingReference = $"REF-{bookingId}",
            ProviderReference = $"provider-{bookingId}",
            Version = 0,
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
            CustomerId = customerId,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await ctx.SaveChangesAsync();
    }
}
