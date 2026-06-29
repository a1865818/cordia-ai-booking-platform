#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.EntityFramework.Concurrency;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Data;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class DepositSessionPersistenceTests(PostgresFixture fixture)
{
    private static DepositSession MakeSession(
        string tenantId,
        string depositSessionId,
        string operationId,
        DepositStatus status = DepositStatus.Created)
        => new()
        {
            TenantId = tenantId,
            DepositSessionId = depositSessionId,
            OperationId = operationId,
            AmountMinor = 5000,
            Currency = "AUD",
            Status = status,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Version = 0,
        };

    // (1) uq_deposit_session_per_operation — two deposit sessions for same operation_id → unique_violation 23505.
    [Fact]
    public async Task Insert_TwoSessionsSameOperation_ThrowsUniqueViolation()
    {
        var tenantId = $"t5-dep-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var dep1 = MakeSession(tenantId, $"dep-{Guid.NewGuid():N}", operationId);
        var dep2 = MakeSession(tenantId, $"dep-{Guid.NewGuid():N}", operationId);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.DepositSessions.Add(dep1);
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = fixture.CreateContext(tenantId);
        ctx2.DepositSessions.Add(dep2);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx2.SaveChangesAsync());
        var pgEx = ex.InnerException as PostgresException;
        Assert.NotNull(pgEx);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pgEx.SqlState);
    }

    // (2) ix_deposit_sessions_expiry partial index exists in pg_indexes.
    [Fact]
    public async Task PartialIndex_IxDepositSessionsExpiry_Exists()
    {
        await using var ctx = fixture.CreateContext("t5-dep-idx");
        var exists = await ctx.Database
            .SqlQueryRaw<int>(
                "SELECT 1 AS value FROM pg_indexes WHERE indexname = 'ix_deposit_sessions_expiry'")
            .AnyAsync();
        Assert.True(exists);
    }

    // (3) Version-token: Created → LinkSent, exactly one wins.
    [Fact]
    public async Task TryTransitionStatusAsync_ExactlyOneWins()
    {
        var tenantId = $"t5-dep-vt-{Guid.NewGuid():N}";
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
            operationId = await TestData.SeedOperationAsync(ctx, tenantId);

        var depId = $"dep-{Guid.NewGuid():N}";
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.DepositSessions.Add(MakeSession(tenantId, depId, operationId, DepositStatus.Created));
            await ctx.SaveChangesAsync();
        }

        await using var ctx1 = fixture.CreateContext(tenantId);
        await using var ctx2 = fixture.CreateContext(tenantId);

        var rows1 = await VersionTokenUpdate.UpdateAsync<DepositSession>(
            ctx1, expectedVersion: 0,
            e => e.TenantId == tenantId && e.DepositSessionId == depId,
            s => s.SetProperty(x => x.Status, DepositStatus.LinkSent));

        var rows2 = await VersionTokenUpdate.UpdateAsync<DepositSession>(
            ctx2, expectedVersion: 0,
            e => e.TenantId == tenantId && e.DepositSessionId == depId,
            s => s.SetProperty(x => x.Status, DepositStatus.LinkSent));

        Assert.Equal(1, rows1 + rows2);

        await using var verifyCtx = fixture.CreateContext(tenantId);
        var reloaded = await verifyCtx.DepositSessions.AsNoTracking()
            .SingleAsync(d => d.DepositSessionId == depId);
        Assert.Equal(DepositStatus.LinkSent, reloaded.Status);
        Assert.Equal(1L, reloaded.Version);
    }

    // (4) Inv-12: deposit_sessions has NO column matching %pan%/%cvv%/%card%; DOES have stripe_session_id + payment_link_url.
    [Fact]
    public async Task DepositSessions_HasNoCardColumns_HasStripeColumns()
    {
        // Use raw ADO.NET to avoid EF scalar mapping issues with string columns.
        await using var conn = new NpgsqlConnection(PostgresFixture.TestConnStr);
        await conn.OpenAsync();

        // Assert no PAN/CVV/card columns exist
        await using var forbiddenCmd = conn.CreateCommand();
        forbiddenCmd.CommandText = """
            SELECT column_name FROM information_schema.columns
            WHERE table_name = 'deposit_sessions'
              AND (column_name LIKE '%pan%' OR column_name LIKE '%cvv%' OR column_name LIKE '%card%')
            """;
        await using var reader = await forbiddenCmd.ExecuteReaderAsync();
        Assert.False(reader.HasRows, "deposit_sessions must not have any PAN/CVV/card columns (Inv-12)");
        await reader.CloseAsync();

        // Assert stripe_session_id exists
        await using var stripeCmd = conn.CreateCommand();
        stripeCmd.CommandText = """
            SELECT COUNT(*)::int FROM information_schema.columns
            WHERE table_name = 'deposit_sessions' AND column_name = 'stripe_session_id'
            """;
        var stripeCount = (int)(await stripeCmd.ExecuteScalarAsync())!;
        Assert.Equal(1, stripeCount);

        // Assert payment_link_url exists
        await using var linkCmd = conn.CreateCommand();
        linkCmd.CommandText = """
            SELECT COUNT(*)::int FROM information_schema.columns
            WHERE table_name = 'deposit_sessions' AND column_name = 'payment_link_url'
            """;
        var linkCount = (int)(await linkCmd.ExecuteScalarAsync())!;
        Assert.Equal(1, linkCount);
    }
}
