#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Data;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class TenantChannelEndpointPersistenceTests(PostgresFixture fixture)
{
    private static TenantChannelEndpoint MakeEndpoint(
        string tenantId,
        string endpointId,
        TenantChannel channel,
        SignalType signalType,
        string signalValue,
        EndpointStatus status = EndpointStatus.Active)
        => new()
        {
            TenantId = tenantId,
            ChannelEndpointId = endpointId,
            Channel = channel,
            SignalType = signalType,
            SignalValue = signalValue,
            ConfigurationVersion = "v1",
            PolicyBundleId = "policy-001",
            Status = status,
            Version = 0,
        };

    // (1) Cross-tenant partial-unique: two ACTIVE rows with same (channel,signal_type,signal_value)
    //     but different tenants → unique_violation 23505 (proves Inv-13 one-signal-one-tenant).
    [Fact]
    public async Task Insert_TwoActiveEndpoints_SameSignal_DifferentTenants_ThrowsUniqueViolation()
    {
        var signal = $"+1555{Guid.NewGuid():N}"[..14]; // unique phone number per test
        var tenantA = $"t5-tce-tA-{Guid.NewGuid():N}";
        var tenantB = $"t5-tce-tB-{Guid.NewGuid():N}";

        await using (var ctxA = fixture.CreateContext(tenantA))
        {
            ctxA.TenantChannelEndpoints.Add(
                MakeEndpoint(tenantA, $"ep-{Guid.NewGuid():N}", TenantChannel.Voice, SignalType.PhoneNumber, signal));
            await ctxA.SaveChangesAsync();
        }

        // Same signal, different tenant — must fail due to uq_channel_signal_active (not tenant-scoped)
        await using var ctxB = fixture.CreateContext(tenantB);
        ctxB.TenantChannelEndpoints.Add(
            MakeEndpoint(tenantB, $"ep-{Guid.NewGuid():N}", TenantChannel.Voice, SignalType.PhoneNumber, signal));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctxB.SaveChangesAsync());
        var pgEx = ex.InnerException as PostgresException;
        Assert.NotNull(pgEx);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pgEx.SqlState);
    }

    // (2) One active + one disabled with SAME signal → both insert OK (partial index only covers active).
    [Fact]
    public async Task Insert_OneActive_OneDisabled_SameSignal_BothSucceed()
    {
        var tenantId = $"t5-tce-mix-{Guid.NewGuid():N}";
        var signal = $"widget-{Guid.NewGuid():N}";

        await using var ctx = fixture.CreateContext(tenantId);
        ctx.TenantChannelEndpoints.Add(
            MakeEndpoint(tenantId, $"ep-{Guid.NewGuid():N}", TenantChannel.Web, SignalType.WidgetKey, signal, EndpointStatus.Active));
        ctx.TenantChannelEndpoints.Add(
            MakeEndpoint(tenantId, $"ep-{Guid.NewGuid():N}", TenantChannel.Web, SignalType.WidgetKey, signal, EndpointStatus.Disabled));

        // Should not throw
        await ctx.SaveChangesAsync();

        var count = await ctx.TenantChannelEndpoints.AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(e => e.TenantId == tenantId && e.SignalValue == signal);
        Assert.Equal(2, count);
    }

    // (3) ResolveActiveAsync returns the active endpoint without any tenant context.
    [Fact]
    public async Task ResolveActiveAsync_ReturnsActiveEndpoint_WithoutTenantContext()
    {
        var tenantId = $"t5-tce-resolve-{Guid.NewGuid():N}";
        var signal = $"+1999{Guid.NewGuid():N}"[..14];
        var endpointId = $"ep-{Guid.NewGuid():N}";

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.TenantChannelEndpoints.Add(
                MakeEndpoint(tenantId, endpointId, TenantChannel.Voice, SignalType.PhoneNumber, signal));
            await ctx.SaveChangesAsync();
        }

        // Use "any" tenant in context — ResolveActiveAsync ignores it
        await using var resolveCtx = fixture.CreateContext("any-tenant");
        var repo = new TenantChannelEndpointRepository(resolveCtx);
        var found = await repo.ResolveActiveAsync(TenantChannel.Voice, SignalType.PhoneNumber, signal);

        Assert.NotNull(found);
        Assert.Equal(tenantId, found.TenantId);
        Assert.Equal(endpointId, found.ChannelEndpointId);
        Assert.Equal(EndpointStatus.Active, found.Status);
    }

    // (4) uq_channel_signal_active index definition does NOT include tenant_id (confirm Inv-13 structural form).
    [Fact]
    public async Task UqChannelSignalActive_IndexDef_DoesNotContainTenantId()
    {
        // Use raw ADO.NET so we can read a plain string column without EF scalar mapping issues.
        await using var conn = new NpgsqlConnection(PostgresFixture.TestConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT indexdef FROM pg_indexes WHERE indexname = 'uq_channel_signal_active'";
        var indexDef = await cmd.ExecuteScalarAsync() as string;

        Assert.NotNull(indexDef);
        // Must NOT contain tenant_id — the index is intentionally not tenant-scoped (Inv-13)
        Assert.DoesNotContain("tenant_id", indexDef, StringComparison.OrdinalIgnoreCase);
        // Must contain the three signal columns
        Assert.Contains("channel", indexDef, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("signal_type", indexDef, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("signal_value", indexDef, StringComparison.OrdinalIgnoreCase);
    }
}
