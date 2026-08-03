#pragma warning disable xUnit1051
using System.Security.Cryptography;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Services.Retention;
using Cordia.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// Spec 14 §24.3 — the cross-tenant retention driver behind
/// RetentionSweepHostedService: enumerates every tenant found in the swept
/// tables and runs all three per-tenant sweeps, so retention is live wiring,
/// not dead code. (Single-tenant sweep semantics are covered by
/// RetentionSweepTests / SecurityRetentionSweepTests.) Also proves the
/// encrypted-object sweep: an expired object is pruned in the same cycle and
/// audited as <c>privacy.retention.object_pruned</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class RetentionSweeperTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed class FixedSecretStore(string? value) : ISecretStore
    {
        public string? GetSecret(string name) => value;

        public string GetRequiredSecret(string name)
            => value ?? throw new SecretNotFoundException(name);
    }

    /// <summary>Hands the sweeper a fresh fixture-backed <see cref="ApplicationDbContext"/>
    /// (and a handler over it) per scope, mirroring production's scoped registrations — and
    /// counts scopes so the test can assert a FRESH one per tenant (the M4 isolation fix).</summary>
    private sealed class FixtureScopeFactory(PostgresFixture fixture) : IServiceScopeFactory
    {
        public int ScopesCreated { get; private set; }

        public IServiceScope CreateScope()
        {
            ScopesCreated++;
            return new FixtureScope(fixture.CreateContext("retention-sweeper-driver"));
        }

        private sealed class FixtureScope(ApplicationDbContext ctx)
            : IServiceScope, IServiceProvider, IAsyncDisposable
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(ApplicationDbContext))
                    return ctx;
                if (serviceType == typeof(RetentionSweepHandler))
                    return new RetentionSweepHandler(ctx);
                return null;
            }

            public void Dispose() => ctx.Dispose();

            // CreateAsyncScope() prefers IAsyncDisposable — implement it so the test disposes the
            // context via the same async path the real DI container uses in production.
            public ValueTask DisposeAsync() => ctx.DisposeAsync();
        }
    }

    /// <summary>Wraps a real object store but throws on <see cref="DeleteAsync"/> for ONE object id,
    /// so the sweep's per-object fault isolation (B5) can be exercised against a live store.</summary>
    private sealed class FailingDeleteStore(IObjectStore inner, string failObjectId) : IObjectStore
    {
        public Task PutAsync(string tenantId, string purpose, string objectId, ReadOnlyMemory<byte> content,
            TimeSpan? ttl = null, CancellationToken ct = default)
            => inner.PutAsync(tenantId, purpose, objectId, content, ttl, ct);

        public Task<byte[]?> GetAsync(string tenantId, string purpose, string objectId, CancellationToken ct = default)
            => inner.GetAsync(tenantId, purpose, objectId, ct);

        public Task<bool> DeleteAsync(string tenantId, string purpose, string objectId, CancellationToken ct = default)
            => objectId == failObjectId
                ? throw new IOException($"simulated delete failure for {objectId}")
                : inner.DeleteAsync(tenantId, purpose, objectId, ct);

        public Task<IReadOnlyList<string>> ListIdsAsync(string tenantId, string purpose, string prefix = "", CancellationToken ct = default)
            => inner.ListIdsAsync(tenantId, purpose, prefix, ct);

        public Task<IReadOnlyList<PrunedObject>> ScanExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
            => inner.ScanExpiredAsync(now, ct);

        public Task<IReadOnlyList<PrunedObject>> SweepExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
            => inner.SweepExpiredAsync(now, ct);
    }

    private static EncryptedFileObjectStore BuildObjectStore(string root)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ObjectStore:Root"] = root,
            })
            .Build();
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new EncryptedFileObjectStore(
            new FixedSecretStore(key), config, NullLogger<EncryptedFileObjectStore>.Instance);
    }

    [Fact]
    public async Task SweepAllTenants_DiscoversAndSweepsEveryTenant()
    {
        var tenantA = TestIds.New("t14-sweepall-a");
        var tenantB = TestIds.New("t14-sweepall-b");

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

        // Tenant A also has an expired encrypted object (e.g. raw audio past its 72h TTL).
        var objectRoot = Path.Combine(Path.GetTempPath(), TestIds.New("cordia-objstore"));
        var objectStore = BuildObjectStore(objectRoot);
        await objectStore.PutAsync(
            tenantA, "raw-audio", "sweepall-audio", new byte[] { 1, 2, 3 },
            ttl: TimeSpan.FromHours(-1)); // already expired

        try
        {
            var scopeFactory = new FixtureScopeFactory(fixture);
            var sweeper = new RetentionSweeper(
                scopeFactory,
                objectStore,
                NullLogger<RetentionSweeper>.Instance);
            var pruned = await sweeper.SweepAllTenantsAsync(Now);
            Assert.True(pruned >= 3, $"expected at least 3 pruned/anonymized rows+objects, got {pruned}");

            // M4: a fresh scope (DbContext) is created for enumeration, for EACH of the >=2
            // discovered tenants, and for the object-audit write — never one shared context.
            Assert.True(
                scopeFactory.ScopesCreated >= 4,
                $"expected a fresh scope per tenant (+ enumerate + object audit), got {scopeFactory.ScopesCreated}");

            // The expired object is gone and its removal is audited (privacy.retention.object_pruned).
            Assert.Null(await objectStore.GetAsync(tenantA, "raw-audio", "sweepall-audio"));
            await using var auditCtx = fixture.CreateContext(tenantA);
            var objectAudit = await auditCtx.AuditEvents.AsNoTracking()
                .IgnoreQueryFilters()
                .SingleAsync(e => e.TenantId == tenantA
                    && e.EventType == "privacy.retention.object_pruned"
                    && e.SubjectRef == "sweepall-audio");
            Assert.Equal("system", objectAudit.Actor);
            Assert.Contains("raw-audio", objectAudit.Metadata);
        }
        finally
        {
            if (Directory.Exists(objectRoot))
                Directory.Delete(objectRoot, recursive: true);
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

    [Fact]
    public async Task ObjectSweep_OneDeleteFails_OtherExpiredObjectsAreStillDeleted()
    {
        // B5: a single DeleteAsync failure must not abort the delete loop — every OTHER expired
        // object still gets pruned this cycle (per-object fault isolation, matching the per-tenant loop).
        var tenant = TestIds.New("t14-delfail");
        var objectRoot = Path.Combine(Path.GetTempPath(), TestIds.New("cordia-objstore"));
        var real = BuildObjectStore(objectRoot);

        // Two expired objects; deletion of "audio-fails" is rigged to throw.
        await real.PutAsync(tenant, "raw-audio", "audio-fails", new byte[] { 1 }, ttl: TimeSpan.FromHours(-1));
        await real.PutAsync(tenant, "raw-audio", "audio-ok", new byte[] { 2 }, ttl: TimeSpan.FromHours(-1));
        var store = new FailingDeleteStore(real, failObjectId: "audio-fails");

        try
        {
            var sweeper = new RetentionSweeper(
                new FixtureScopeFactory(fixture), store, NullLogger<RetentionSweeper>.Instance);

            // The sweep must not throw despite the rigged delete failure.
            await sweeper.SweepAllTenantsAsync(Now);

            // The healthy object was pruned; the rigged one survives (its delete threw) and will be
            // re-identified and re-deleted next cycle — but it never blocked its sibling. Both are
            // expired, so GetAsync returns null for either regardless of deletion; ScanExpiredAsync
            // is what distinguishes a still-on-disk blob (re-identified) from a deleted one (gone).
            var stillExpiredOnDisk = (await real.ScanExpiredAsync(Now)).Select(o => o.ObjectId).ToList();
            Assert.DoesNotContain("audio-ok", stillExpiredOnDisk);
            Assert.Contains("audio-fails", stillExpiredOnDisk);

            // Both removals were audited BEFORE the deletes (audit-first, L1) — a blob left behind
            // still carries a trail.
            await using var auditCtx = fixture.CreateContext(tenant);
            var audited = await auditCtx.AuditEvents.AsNoTracking()
                .IgnoreQueryFilters()
                .Where(e => e.TenantId == tenant && e.EventType == "privacy.retention.object_pruned")
                .Select(e => e.SubjectRef)
                .ToListAsync();
            Assert.Contains("audio-ok", audited);
            Assert.Contains("audio-fails", audited);
        }
        finally
        {
            if (Directory.Exists(objectRoot))
                Directory.Delete(objectRoot, recursive: true);
        }
    }
}
