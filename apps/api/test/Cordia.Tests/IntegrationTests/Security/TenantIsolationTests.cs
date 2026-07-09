#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

using Cordia.Tests.IntegrationTests.Persistence;

namespace Cordia.Tests.IntegrationTests.Security;

/// <summary>
/// Spec 14 §27.6 / Inv-13 — "tenant isolation across every repository and queue
/// consumer". The guard is STRUCTURAL: every ITenantScoped entity gets the EF
/// global tenant query filter, so a new repository cannot silently bypass it.
/// This suite (a) proves the filter exists for EVERY mapped ITenantScoped
/// entity — a new unfiltered entity fails the test and blocks merge — and
/// (b) live-probes representative aggregates across two tenants.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class TenantIsolationTests(PostgresFixture fixture)
{
    [Fact]
    public void EveryTenantScopedEntity_HasTheGlobalTenantQueryFilter()
    {
        using var ctx = fixture.CreateContext("tenant-structural-check");

        var unguarded = ctx.Model.GetEntityTypes()
            .Where(et => typeof(ITenantScoped).IsAssignableFrom(et.ClrType))
            .Where(et => et.GetDeclaredQueryFilters().Count == 0)
            .Select(et => et.ClrType.Name)
            .ToList();

        Assert.True(
            unguarded.Count == 0,
            $"ITenantScoped entities WITHOUT a tenant query filter (Inv-13 violation): {string.Join(", ", unguarded)}");
    }

    [Fact]
    public void EveryMappedEntity_IsTenantScoped_OrOnTheJustifiedAllowlist()
    {
        // Entities that legitimately have no ambient tenant filter. Additions
        // here need a written justification — this list is the merge gate.
        string[] allowlist =
        [
            // Spec 15 tenant-RESOLUTION registry: the gateway looks up the row
            // by channel endpoint to DISCOVER the tenant, i.e. before any
            // tenant scope exists — an ambient tenant filter would break
            // resolution. Rows still carry tenant_id; access is confined to
            // the resolution path (TenantChannelEndpointRepository).
            nameof(TenantChannelEndpoint),
        ];

        using var ctx = fixture.CreateContext("tenant-structural-check");

        var unscoped = ctx.Model.GetEntityTypes()
            .Where(et => !typeof(ITenantScoped).IsAssignableFrom(et.ClrType))
            .Select(et => et.ClrType.Name)
            .Except(allowlist)
            .ToList();

        Assert.True(
            unscoped.Count == 0,
            $"Mapped entities that are neither ITenantScoped nor allowlisted: {string.Join(", ", unscoped)}");
    }

    [Fact]
    public async Task TenantB_CannotReadTenantAsRows_ThroughTheAmbientFilter()
    {
        var tenantA = $"t14-iso-a-{Guid.NewGuid():N}";
        var tenantB = $"t14-iso-b-{Guid.NewGuid():N}";

        await using (var ctxA = fixture.CreateContext(tenantA))
        {
            ctxA.ConversationSessions.Add(new ConversationSession
            {
                TenantId = tenantA,
                SessionId = "iso-sess",
                Channel = TenantChannel.Voice,
                StartedAt = DateTimeOffset.UtcNow,
            });
            ctxA.AuditEvents.Add(new AuditEvent
            {
                TenantId = tenantA,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "test.iso",
                Actor = "system",
                SubjectRef = "iso-sess",
                OccurredAt = DateTimeOffset.UtcNow,
            });
            ctxA.VerificationGrants.Add(new VerificationGrant
            {
                TenantId = tenantA,
                GrantId = "iso-grant",
                CustomerRef = "cust-iso",
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            });
            await ctxA.SaveChangesAsync();
        }

        await using var ctxB = fixture.CreateContext(tenantB);
        Assert.False(await ctxB.ConversationSessions.AnyAsync(s => s.SessionId == "iso-sess"));
        Assert.False(await ctxB.AuditEvents.AnyAsync(e => e.EventType == "test.iso" && e.SubjectRef == "iso-sess"));
        Assert.False(await ctxB.VerificationGrants.AnyAsync(g => g.GrantId == "iso-grant"));

        // And the owning tenant still sees its rows (the filter scopes, not hides).
        await using var ctxA2 = fixture.CreateContext(tenantA);
        Assert.True(await ctxA2.ConversationSessions.AnyAsync(s => s.SessionId == "iso-sess"));
    }
}
