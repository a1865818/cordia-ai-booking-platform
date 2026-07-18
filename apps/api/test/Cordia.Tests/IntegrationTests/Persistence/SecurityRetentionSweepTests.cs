#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.Services.Retention;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// Spec-14 retention sweeps: session anonymization (tombstone, never delete)
/// and verification-grant pruning, each audited (§24.3, Inv-14).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class SecurityRetentionSweepTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private async Task SeedSessionAsync(
        string tenantId, string sessionId, DateTimeOffset? endedAt, string? customerRef)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.ConversationSessions.Add(new ConversationSession
        {
            TenantId = tenantId,
            SessionId = sessionId,
            Channel = TenantChannel.Voice,
            StartedAt = Now.AddDays(-120),
            EndedAt = endedAt,
            CustomerRef = customerRef,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedGrantAsync(
        string tenantId, string grantId, DateTimeOffset expiresAt, DateTimeOffset? consumedAt)
    {
        await using var ctx = fixture.CreateContext(tenantId);
        ctx.VerificationGrants.Add(new VerificationGrant
        {
            TenantId = tenantId,
            GrantId = grantId,
            CustomerRef = "cust-1",
            AllowedAction = VerificationActionEnum.Modify,
            IssuedAt = Now.AddDays(-120),
            ExpiresAt = expiresAt,
            ConsumedAt = consumedAt,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task SessionSweep_AnonymizesEndedSessionsPastWindow_PreservesRowAndAudits()
    {
        var tenantId = $"t14-ret-s-{Guid.NewGuid():N}";
        await SeedSessionAsync(tenantId, "old-ended", Now.AddDays(-100), "cust-old");
        await SeedSessionAsync(tenantId, "recent-ended", Now.AddDays(-5), "cust-recent");
        await SeedSessionAsync(tenantId, "still-live", null, "cust-live");

        await using var ctx = fixture.CreateContext(tenantId);
        var result = await new RetentionSweepHandler(ctx)
            .SweepConversationSessionsAsync(tenantId, Now);

        Assert.Equal(1, result.Pruned);

        await using var readCtx = fixture.CreateContext(tenantId);
        var old = await readCtx.ConversationSessions.AsNoTracking()
            .SingleAsync(s => s.SessionId == "old-ended");
        Assert.Null(old.CustomerRef);            // tombstoned
        Assert.NotNull(old.EndedAt);             // row preserved (never hard-deleted)
        Assert.Equal(1L, old.Version);

        var recent = await readCtx.ConversationSessions.AsNoTracking()
            .SingleAsync(s => s.SessionId == "recent-ended");
        Assert.Equal("cust-recent", recent.CustomerRef);
        var live = await readCtx.ConversationSessions.AsNoTracking()
            .SingleAsync(s => s.SessionId == "still-live");
        Assert.Equal("cust-live", live.CustomerRef);

        var audit = await readCtx.AuditEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EventType == "privacy.retention.session_anonymized")
            .SingleAsync();
        Assert.Equal("old-ended", audit.SubjectRef);

        // Idempotent: a second sweep finds no candidates and writes no more audits.
        await using var ctx2 = fixture.CreateContext(tenantId);
        var second = await new RetentionSweepHandler(ctx2)
            .SweepConversationSessionsAsync(tenantId, Now);
        Assert.Equal(0, second.Pruned);
    }

    [Fact]
    public async Task GrantSweep_PrunesExpiredAndConsumedPastWindow_KeepsActive_AndAudits()
    {
        var tenantId = $"t14-ret-g-{Guid.NewGuid():N}";
        await SeedGrantAsync(tenantId, "grant-expired-old", Now.AddDays(-40), null);
        await SeedGrantAsync(tenantId, "grant-consumed-old", Now.AddDays(-40), Now.AddDays(-40));
        await SeedGrantAsync(tenantId, "grant-expired-recent", Now.AddDays(-5), null);
        await SeedGrantAsync(tenantId, "grant-active", Now.AddHours(1), null);

        await using var ctx = fixture.CreateContext(tenantId);
        var result = await new RetentionSweepHandler(ctx)
            .SweepVerificationGrantsAsync(tenantId, Now);

        Assert.Equal(2, result.Pruned);

        await using var readCtx = fixture.CreateContext(tenantId);
        var remaining = await readCtx.VerificationGrants.AsNoTracking()
            .Where(g => g.TenantId == tenantId)
            .Select(g => g.GrantId)
            .ToListAsync();
        Assert.Equal(
            new[] { "grant-active", "grant-expired-recent" },
            remaining.OrderBy(g => g).ToArray());

        var audits = await readCtx.AuditEvents.AsNoTracking()
            .Where(e => e.TenantId == tenantId && e.EventType == "privacy.retention.grant_pruned")
            .Select(e => e.SubjectRef)
            .ToListAsync();
        Assert.Equal(
            new[] { "grant-consumed-old", "grant-expired-old" },
            audits.OrderBy(a => a).ToArray());
    }
}
