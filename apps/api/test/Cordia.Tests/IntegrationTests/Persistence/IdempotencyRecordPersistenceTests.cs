#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cordia.Tests.IntegrationTests.Persistence;

[Trait("Category", "Integration")]
[Collection("Postgres")]
public class IdempotencyRecordPersistenceTests(PostgresFixture fixture)
{
    // ── 1. Round-trip: record persists and enum values survive a write→read cycle ──
    [Fact]
    public async Task AddAsync_ThenGetByKey_RoundTrips()
    {
        var tenantId = $"t1-idem-{Guid.NewGuid():N}";
        var key = $"key-{Guid.NewGuid():N}";
        var deadline = DateTimeOffset.UtcNow.AddDays(365);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.IdempotencyRecords.Add(new IdempotencyRecord
            {
                TenantId = tenantId,
                IdempotencyKey = key,
                OperationType = OperationTypeEnum.Create,
                RequestHash = "hash-abc",
                OperationId = "op-001",
                ProcessingStatus = IdempotencyStatus.InProgress,
                RetentionDeadline = deadline,
            });
            await ctx.SaveChangesAsync();
        }

        await using var readCtx = fixture.CreateContext(tenantId);
        var record = await readCtx.IdempotencyRecords
            .AsNoTracking()
            .SingleAsync(r => r.IdempotencyKey == key);

        Assert.Equal(tenantId, record.TenantId);
        Assert.Equal(OperationTypeEnum.Create, record.OperationType);
        Assert.Equal(IdempotencyStatus.InProgress, record.ProcessingStatus);
        Assert.Equal("hash-abc", record.RequestHash);
    }

    // ── 2. Duplicate PK throws unique_violation (SqlState 23505) ──────────────
    [Fact]
    public async Task AddAsync_DuplicateKey_ThrowsUniqueViolation()
    {
        var tenantId = $"t1-idem-{Guid.NewGuid():N}";
        var key = $"key-{Guid.NewGuid():N}";
        var deadline = DateTimeOffset.UtcNow.AddDays(365);

        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.IdempotencyRecords.Add(new IdempotencyRecord
            {
                TenantId = tenantId,
                IdempotencyKey = key,
                OperationType = OperationTypeEnum.Create,
                RequestHash = "hash-abc",
                OperationId = "op-001",
                ProcessingStatus = IdempotencyStatus.InProgress,
                RetentionDeadline = deadline,
            });
            await ctx.SaveChangesAsync();
        }

        await using var ctx2 = fixture.CreateContext(tenantId);
        ctx2.IdempotencyRecords.Add(new IdempotencyRecord
        {
            TenantId = tenantId,
            IdempotencyKey = key, // same key — must conflict
            OperationType = OperationTypeEnum.Create,
            RequestHash = "hash-xyz",
            OperationId = "op-002",
            ProcessingStatus = IdempotencyStatus.InProgress,
            RetentionDeadline = deadline,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx2.SaveChangesAsync());
        var pgEx = ex.InnerException as PostgresException;
        Assert.NotNull(pgEx);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, pgEx.SqlState);
    }
}
