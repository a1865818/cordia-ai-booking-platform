#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

using System.Net;
using System.Text.Json;
using Alba;
using Cordia.Domain.Entities;
using Cordia.Domain.TenantResolution;
using Cordia.Tests.IntegrationTests.Acceptance;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Tests.IntegrationTests.Auth;

/// <summary>
/// G1 web-origin authentication (Alba, Collection("AcceptanceApi")). Tests insert their own
/// operators with <c>PasswordHasher&lt;Operator&gt;</c> so they do not depend on seed-dev-tenant.sql.
/// </summary>
[Trait("Category", "Integration")]
[Collection("AcceptanceApi")]
public class WebOriginAuthTests(AcceptanceApiFixture fixture)
{
    private const string Password = "localdev";
    private const string LoginUrl = "/api/auth/login";
    private const string SessionUrl = "/api/auth/session";
    private const string LogoutUrl = "/api/auth/logout";

    [Fact]
    public async Task Login_Returns200_JwsVerifies_AndCreatesSessionRow()
    {
        var (tenantId, email, operatorId) = await SeedOperatorAsync();

        var result = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { email, password = Password }).ToUrl(LoginUrl);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        var body = ReadLogin(await result.ReadAsTextAsync());
        Assert.False(string.IsNullOrEmpty(body.TenantContext));
        Assert.Equal(operatorId, body.ActorId);
        Assert.Equal("owner", body.ActorRole);
        Assert.Equal(tenantId, body.TenantId);
        Assert.True(body.ExpiresAt > DateTimeOffset.UtcNow);

        var signer = fixture.Host.Services.GetRequiredService<IOperatorSessionSigner>();
        var claims = signer.Verify(body.TenantContext);
        Assert.Equal(tenantId, claims.TenantId);
        Assert.Equal(operatorId, claims.ActorId);
        Assert.Equal("owner", claims.ActorRole);
        Assert.False(string.IsNullOrEmpty(claims.SessionId));

        await using var ctx = fixture.CreateContext(tenantId);
        var session = await ctx.OperatorSessions.AsNoTracking()
            .SingleAsync(s => s.TenantId == tenantId && s.SessionId == claims.SessionId);
        Assert.Equal(operatorId, session.OperatorId);
        Assert.Null(session.RevokedAt);
        Assert.Equal(body.ExpiresAt, session.ExpiresAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Login_ThenConsentPolicies_WithOperatorContext_AndNoTenantIdHeader_Returns200()
    {
        var (_, email, _) = await SeedOperatorAsync();
        var login = await LoginAsync(email);
        var jws = ReadLogin(await login.ReadAsTextAsync()).TenantContext;

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url("/api/consent-policies?channel=web");
            x.WithRequestHeader("X-Tenant-Context", jws);
            // No X-Tenant-Id — tenant must come from the verified opctx alone.
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });
    }

    [Fact]
    public async Task WrongPassword_AndUnknownEmail_ReturnIdentical401_AndCreateNoSession()
    {
        var (tenantId, email, _) = await SeedOperatorAsync();
        var unknownEmail = $"nobody-{TestIds.New("auth")}@test.local";

        var wrong = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { email, password = "not-the-password" }).ToUrl(LoginUrl);
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });
        var unknown = await fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { email = unknownEmail, password = Password }).ToUrl(LoginUrl);
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });

        var wrongBody = await wrong.ReadAsTextAsync();
        var unknownBody = await unknown.ReadAsTextAsync();
        Assert.Equal(wrongBody, unknownBody);
        Assert.Contains("Invalid credentials", wrongBody, StringComparison.Ordinal);

        await using var ctx = fixture.CreateContext(tenantId);
        var sessionCount = await ctx.OperatorSessions.AsNoTracking()
            .CountAsync(s => s.TenantId == tenantId);
        Assert.Equal(0, sessionCount);
    }

    [Fact]
    public async Task Logout_Revokes_ReuseJwsIs401_SecondLogoutIs204()
    {
        var (tenantId, email, _) = await SeedOperatorAsync();
        var login = await LoginAsync(email);
        var jws = ReadLogin(await login.ReadAsTextAsync()).TenantContext;

        await fixture.Host.Scenario(x =>
        {
            x.Post.Url(LogoutUrl);
            x.WithRequestHeader("X-Tenant-Context", jws);
            x.StatusCodeShouldBe(HttpStatusCode.NoContent);
        });

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var session = await ctx.OperatorSessions.AsNoTracking()
                .SingleAsync(s => s.TenantId == tenantId);
            Assert.NotNull(session.RevokedAt);
        }

        await fixture.Host.Scenario(x =>
        {
            x.Get.Url(SessionUrl);
            x.WithRequestHeader("X-Tenant-Context", jws);
            x.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
        });

        await fixture.Host.Scenario(x =>
        {
            x.Post.Url(LogoutUrl);
            x.WithRequestHeader("X-Tenant-Context", jws);
            x.StatusCodeShouldBe(HttpStatusCode.NoContent);
        });
    }

    [Fact]
    public async Task GetSession_AfterLogin_ReturnsActorAndExpiry()
    {
        var (tenantId, email, operatorId) = await SeedOperatorAsync();
        var login = await LoginAsync(email);
        var body = ReadLogin(await login.ReadAsTextAsync());

        var result = await fixture.Host.Scenario(x =>
        {
            x.Get.Url(SessionUrl);
            x.WithRequestHeader("X-Tenant-Context", body.TenantContext);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

        using var doc = JsonDocument.Parse(await result.ReadAsTextAsync());
        Assert.Equal(operatorId, doc.RootElement.GetProperty("actor_id").GetString());
        Assert.Equal("owner", doc.RootElement.GetProperty("actor_role").GetString());
        Assert.Equal(tenantId, doc.RootElement.GetProperty("tenant_id").GetString());
        Assert.True(doc.RootElement.TryGetProperty("expires_at", out _));
    }

    private async Task<(string TenantId, string Email, string OperatorId)> SeedOperatorAsync(
        string role = "owner")
    {
        var tenantId = TestIds.New("auth-t");
        var operatorId = TestIds.New("opr");
        var email = $"op-{TestIds.New("auth")}@test.local";
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

    private Task<IScenarioResult> LoginAsync(string email)
        => fixture.Host.Scenario(x =>
        {
            x.Post.Json(new { email, password = Password }).ToUrl(LoginUrl);
            x.StatusCodeShouldBe(HttpStatusCode.OK);
        });

    private readonly record struct LoginBody(
        string TenantContext, string ActorId, string ActorRole, string TenantId, DateTimeOffset ExpiresAt);

    private static LoginBody ReadLogin(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new LoginBody(
            TenantContext: root.GetProperty("tenant_context").GetString()!,
            ActorId: root.GetProperty("actor_id").GetString()!,
            ActorRole: root.GetProperty("actor_role").GetString()!,
            TenantId: root.GetProperty("tenant_id").GetString()!,
            ExpiresAt: root.GetProperty("expires_at").GetDateTimeOffset());
    }
}
