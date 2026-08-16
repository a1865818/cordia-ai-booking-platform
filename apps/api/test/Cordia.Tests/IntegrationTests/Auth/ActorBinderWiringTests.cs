#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Tests.IntegrationTests.Auth;

/// <summary>
/// Regression coverage for a review finding on the auth PR: <c>ActorBinder</c> existed but no
/// controller called it, so a verified operator session was never consulted for authorization —
/// role came from the forgeable <c>X-Actor-Role</c> header alone, letting an authenticated
/// operator-role session escalate to owner-only actions by adding a header. Every controller now
/// resolves through <c>ActorBinder</c>; these tests exercise that end to end (real login → real
/// session → a spoofed header on the wire) rather than the helper in isolation, which is exactly
/// what let the missing wiring pass CI the first time.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class ActorBinderWiringTests(AcceptanceApiFixture fixture)
{
    private const string Password = "localdev";

    [Fact]
    public async Task VerifiedOperatorSession_CannotEscalateToOwnerViaSpoofedHeader()
    {
        var (tenantId, email, operatorId) = await SeedOperatorAsync("operator");
        var jws = await LoginAsync(email);
        var campaignId = await CreateCampaignAsOwnerAsync(tenantId);

        // The exact reproduction from the review: a valid operator session, plus a client-chosen
        // X-Actor-Role: owner header. If ActorBinder is actually consulted, the verified session's
        // "operator" role wins and this is refused — the header must not be able to override it.
        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/campaigns/{campaignId}/approve");
            x.WithRequestHeader("X-Tenant-Context", jws);
            x.WithRequestHeader("X-Actor", "someone-else");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.WithRequestHeader("X-Reason-Code", "trying it on");
            x.StatusCodeShouldBe(HttpStatusCode.Forbidden);
        });

        // Audit integrity: the denial must name the REAL authenticated operator, never the
        // spoofed X-Actor header value — otherwise the trail itself is forgeable even though a
        // verified identity was available.
        await using var ctx = fixture.CreateContext(tenantId);
        var events = await ctx.AuditEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EventType == "campaign.approved")
            .OrderByDescending(e => e.OccurredAt)
            .ToListAsync();

        var deny = events.FirstOrDefault(e => Decision(e) == "deny");
        Assert.NotNull(deny);
        Assert.Equal(operatorId, deny!.Actor);
        Assert.NotEqual("someone-else", deny.Actor);
    }

    [Fact]
    public async Task VerifiedOwnerSession_CanApprove_WithNoHeadersAtAll()
    {
        // The other half: a real owner session must be sufficient on its own — the fix must not
        // regress into requiring the shim headers alongside a verified session.
        var (tenantId, email, operatorId) = await SeedOperatorAsync("owner");
        var jws = await LoginAsync(email);
        var campaignId = await CreateCampaignAsOwnerAsync(tenantId);

        await fixture.Host.Scenario(x =>
        {
            x.Post.RawJson("{}").ToUrl($"/api/campaigns/{campaignId}/approve");
            x.WithRequestHeader("X-Tenant-Context", jws);
            x.WithRequestHeader("X-Reason-Code", "owner approval");
            x.StatusCodeShouldBeOk();
        });

        await using var ctx = fixture.CreateContext(tenantId);
        var events = await ctx.AuditEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EventType == "campaign.approved")
            .OrderByDescending(e => e.OccurredAt)
            .ToListAsync();

        var allow = events.FirstOrDefault(e => Decision(e) == "allow");
        Assert.NotNull(allow);
        Assert.Equal(operatorId, allow!.Actor);
    }

    /// <summary>The envelope packs decision into the metadata jsonb (see AuditEnvelope.Build).</summary>
    private static string? Decision(Cordia.Domain.Entities.AuditEvent e)
    {
        using var doc = JsonDocument.Parse(e.Metadata);
        return doc.RootElement.TryGetProperty("decision", out var value) ? value.GetString() : null;
    }

    private async Task<(string TenantId, string Email, string OperatorId)> SeedOperatorAsync(string role)
    {
        var tenantId = TestIds.New("actorbind-t");
        var operatorId = TestIds.New("opr");
        var email = $"op-{TestIds.New("actorbind")}@test.local";
        var hasher = fixture.Host.Services.GetRequiredService<IPasswordHasher<Operator>>();
        var now = DateTimeOffset.UtcNow;

        var op = new Operator
        {
            TenantId = tenantId,
            OperatorId = operatorId,
            Email = email,
            Role = role,
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        };
        op.PasswordHash = hasher.HashPassword(op, Password);

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.Operators.Add(op);
        await ctx.SaveChangesAsync();

        return (tenantId, email, operatorId);
    }

    private async Task<string> LoginAsync(string email)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { email, password = Password }).ToUrl("/api/auth/login");
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        return doc.RootElement.GetProperty("tenant_context").GetString()!;
    }

    /// <summary>
    /// Seeds a campaign via the header shim (still supported for the agent/dev-shim path) so the
    /// test can exercise the SESSION path in isolation on the /approve call itself.
    /// </summary>
    private async Task<string> CreateCampaignAsOwnerAsync(string tenantId)
    {
        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new
            {
                title = "Win back no-shows",
                audience = "customers with a no-show in the last 60 days",
                channel = "sms",
                message = "Hi {name}, we miss you. Reply BOOK.",
                recipients = 8,
            }).ToUrl("/api/campaigns");
            x.WithRequestHeader("X-Tenant-Id", tenantId);
            x.WithRequestHeader("X-Actor", "owner@example.com");
            x.WithRequestHeader("X-Actor-Role", "owner");
            x.WithRequestHeader("X-Reason-Code", "campaign_setup");
            x.StatusCodeShouldBe(HttpStatusCode.Created);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }
}
