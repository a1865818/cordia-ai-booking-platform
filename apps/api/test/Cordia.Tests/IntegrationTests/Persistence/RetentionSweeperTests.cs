#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.Services.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// Spec 14 §24.3 — the cross-tenant retention driver behind
/// RetentionSweepHostedService: enumerates every tenant found in the swept
/// tables and runs all three per-tenant sweeps, so retention is live wiring,
/// not dead code. (Single-tenant sweep semantics are covered by
/// RetentionSweepTests / SecurityRetentionSweepTests.)
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class RetentionSweeperTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task SweepAllTenants_DiscoversAndSweepsEveryTenant()
    {
        var tenantA = $"t14-sweepall-a-{Guid.NewGuid():N}";
        var tenantB = $"t14-sweepall-b-{Guid.NewGuid():N}";

        // Tenant A: an ended session past the anonymization window.
        await using (var ctx = fixture.CreateContext(tenantA))
        {
            ctx.ConversationSessions.Add(new ConversationSession
            {
                TenantId = tenantA,
                SessionId = "sweepall-sess",
                Channel = TenantChannel.Voice,
                StartedAt = Now.AddDays(-120),
                EndedAt = Now.AddDays(-100),
                CustomerRef = "cust-a",
            });
            await ctx.SaveChangesAsync();
        }

        // Tenant B: an expired grant past the prune window.
        await using (var ctx = fixture.CreateContext(tenantB))
        {
            ctx.VerificationGrants.Add(new VerificationGrant
            {
                TenantId = tenantB,
                GrantId = "sweepall-grant",
                CustomerRef = "cust-b",
                AllowedAction = VerificationActionEnum.Modify,
                IssuedAt = Now.AddDays(-120),
                ExpiresAt = Now.AddDays(-40),
            });
            await ctx.SaveChangesAsync();
        }

        await using (var sweepCtx = fixture.CreateContext("retention-sweeper-driver"))
        {
            var sweeper = new RetentionSweeper(
                sweepCtx,
                new RetentionSweepHandler(sweepCtx),
                NullLogger<RetentionSweeper>.Instance);
            var pruned = await sweeper.SweepAllTenantsAsync(Now);
            Assert.True(pruned >= 2, $"expected at least 2 pruned/anonymized rows, got {pruned}");
        }

        // Tenant A's session anonymized; tenant B's grant pruned — one cycle, both tenants.
        await using var readCtx = fixture.CreateContext(tenantA);
        var session = await readCtx.ConversationSessions.AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(s => s.TenantId == tenantA && s.SessionId == "sweepall-sess");
        Assert.Null(session.CustomerRef);

        var grantGone = !await readCtx.VerificationGrants.AsNoTracking()
            .IgnoreQueryFilters()
            .AnyAsync(g => g.TenantId == tenantB && g.GrantId == "sweepall-grant");
        Assert.True(grantGone, "tenant B's expired grant should have been pruned");
    }
}
