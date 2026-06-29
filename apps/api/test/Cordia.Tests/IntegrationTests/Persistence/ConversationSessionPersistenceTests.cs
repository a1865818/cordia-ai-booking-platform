#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ConversationSessionPersistenceTests(PostgresFixture fixture)
{
    // ── 1. Round-trip: pg-enum values survive a write→read cycle ──────────
    [Fact]
    public async Task RoundTrip_ConsentStateAndChannel_ArePreserved()
    {
        var tenantId = $"t0-tenant-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.ConversationSessions.Add(new ConversationSession
            {
                TenantId = tenantId,
                SessionId = sessionId,
                Channel = TenantChannel.Voice,
                ConsentState = ConsentState.NotRequired,
                StartedAt = DateTimeOffset.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var session = await readCtx.ConversationSessions
            .AsNoTracking()
            .SingleAsync(s => s.SessionId == sessionId);

        Assert.Equal(TenantChannel.Voice, session.Channel);
        Assert.Equal(ConsentState.NotRequired, session.ConsentState);
    }

    // ── 2. Tenant isolation: global query filter hides cross-tenant rows ──
    [Fact]
    public async Task TenantIsolation_CrossTenantRowIsInvisible()
    {
        var tenantA = $"t0-a-{Guid.NewGuid():N}";
        var tenantB = $"t0-b-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";

        await using (var ctxA = fixture.CreateContext(tenantA))
        {
            ctxA.ConversationSessions.Add(new ConversationSession
            {
                TenantId = tenantA,
                SessionId = sessionId,
                Channel = TenantChannel.Web,
                StartedAt = DateTimeOffset.UtcNow,
            });
            await ctxA.SaveChangesAsync();
        }

        // Tenant B must not see tenant A's row.
        await using var ctxB = fixture.CreateContext(tenantB);
        var visibleToB = await ctxB.ConversationSessions
            .AsNoTracking()
            .AnyAsync(s => s.SessionId == sessionId);

        Assert.False(visibleToB);

        // Tenant A can still see it.
        await using var ctxA2 = fixture.CreateContext(tenantA);
        var visibleToA = await ctxA2.ConversationSessions
            .AsNoTracking()
            .AnyAsync(s => s.SessionId == sessionId);

        Assert.True(visibleToA);
    }

    // ── 3. Version-token concurrency: exactly one update wins ─────────────
    [Fact]
    public async Task VersionToken_ExactlyOneUpdateWins()
    {
        var tenantId = $"t0-tenant-{Guid.NewGuid():N}";
        var sessionId = $"s-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.ConversationSessions.Add(new ConversationSession
            {
                TenantId = tenantId,
                SessionId = sessionId,
                Channel = TenantChannel.Messaging,
                StartedAt = DateTimeOffset.UtcNow,
                Version = 0,
            });
            await ctx.SaveChangesAsync();
        }

        // Two concurrent updates from version 0 (exercises the SetProperty + owned
        // version-bump composition path of the generic helper).
        await using var ctx1 = fixture.CreateContext(tenantId);
        await using var ctx2 = fixture.CreateContext(tenantId);

        var rows1 = await VersionTokenUpdate.UpdateAsync<ConversationSession>(
            ctx1, expectedVersion: 0,
            e => e.TenantId == tenantId && e.SessionId == sessionId,
            s => s.SetProperty(x => x.EndedAt, DateTimeOffset.UtcNow));

        var rows2 = await VersionTokenUpdate.UpdateAsync<ConversationSession>(
            ctx2, expectedVersion: 0,
            e => e.TenantId == tenantId && e.SessionId == sessionId,
            s => s.SetProperty(x => x.EndedAt, DateTimeOffset.UtcNow));

        Assert.Equal(1, rows1 + rows2); // exactly one wins
        Assert.True(VersionTokenUpdate.ShouldReload(rows1) || VersionTokenUpdate.ShouldReload(rows2));

        // The winning update must have bumped version 0 → 1 (the helper owns the bump).
        await using var verifyCtx = fixture.CreateContext(tenantId);
        var reloaded = await verifyCtx.ConversationSessions
            .AsNoTracking()
            .SingleAsync(s => s.SessionId == sessionId);
        Assert.Equal(1, reloaded.Version);
        Assert.NotNull(reloaded.EndedAt);
    }

    // ── 4. Snake_case columns: information_schema confirms column names ────
    [Fact]
    public async Task SnakeCaseColumns_ExistInInformationSchema()
    {
        var expectedColumns = new[]
        {
            "tenant_id", "session_id", "consent_state",
            "consent_recorded_at", "recording_allowed",
        };

        await using var conn = new Npgsql.NpgsqlConnection(PostgresFixture.TestConnStr);
        await conn.OpenAsync();

        foreach (var column in expectedColumns)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*)::int FROM information_schema.columns " +
                $"WHERE table_name = 'conversation_sessions' AND column_name = '{column}'";
            var count = (int)(await cmd.ExecuteScalarAsync())!;

            Assert.True(count > 0, $"Expected column '{column}' not found in conversation_sessions.");
        }
    }
}
