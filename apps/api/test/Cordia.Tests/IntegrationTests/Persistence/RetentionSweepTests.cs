#pragma warning disable xUnit1051
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Services.Retention;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// Integration tests for <see cref="RetentionSweepHandler"/> against a real PostgreSQL database.
/// Each test uses a unique tenant to ensure full isolation.
/// Verifies spec 12 §6.3 / Inv-14: prune past-deadline terminal records, skip open-work records,
/// skip not-yet-due records, and emit audit_events for each pruned record.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class RetentionSweepTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // ──────────────────────────────────────────────────────────────────────────
    // (1) Happy path: terminal op + past deadline → pruned + audit event emitted.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Sweep_PrunesRecord_AndEmitsAuditEvent_WhenTerminalOpAndPastDeadline()
    {
        var tenantId = TestIds.New("t6-sw");
        var iKey = TestIds.New("ikey");

        // Seed a terminal operation (Booked).
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(
                ctx, tenantId, OperationStatus.Booked, TestContext.Current.CancellationToken);
        }

        // Insert idempotency_record with retention_deadline in the past.
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.IdempotencyRecords.Add(new IdempotencyRecord
            {
                TenantId = tenantId,
                IdempotencyKey = iKey,
                OperationType = Domain.Generated.OperationTypeEnum.Create,
                RequestHash = "h",
                OperationId = operationId,
                ProcessingStatus = IdempotencyStatus.Completed,
                CreatedAt = Now.AddDays(-10),
                LastSeenAt = Now.AddDays(-10),
                RetentionDeadline = Now.AddDays(-1),
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Run sweep.
        RetentionSweepResult result;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var handler = new RetentionSweepHandler(ctx);
            result = await handler.SweepIdempotencyRecordsAsync(
                tenantId, Now, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, result.Pruned);
        Assert.Equal(0, result.Skipped);

        // Record must be gone.
        await using (var readCtx = fixture.CreateContext(tenantId))
        {
            var remaining = await readCtx.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.IdempotencyKey == iKey,
                    TestContext.Current.CancellationToken);
            Assert.Null(remaining);
        }

        // Audit event must exist.
        await using (var readCtx = fixture.CreateContext(tenantId))
        {
            var auditRow = await readCtx.AuditEvents
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    e => e.EventType == "privacy.retention.idempotency_pruned"
                         && e.SubjectRef == iKey,
                    TestContext.Current.CancellationToken);
            Assert.NotNull(auditRow);
            Assert.Equal("system", auditRow.Actor);
            Assert.Equal(operationId, auditRow.OperationId);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (2) Open dispute guard (Inv-14): non-terminal op → record skipped, NOT pruned.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Sweep_SkipsRecord_WhenLinkedOperationIsNonTerminal()
    {
        var tenantId = TestIds.New("t6-skip");
        var iKey = TestIds.New("ikey");

        // Seed a non-terminal operation (ManualReview — open dispute/SLA window).
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(
                ctx, tenantId, OperationStatus.ManualReview, TestContext.Current.CancellationToken);
        }

        // Insert idempotency_record with past retention_deadline.
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.IdempotencyRecords.Add(new IdempotencyRecord
            {
                TenantId = tenantId,
                IdempotencyKey = iKey,
                OperationType = Domain.Generated.OperationTypeEnum.Create,
                RequestHash = "h",
                OperationId = operationId,
                ProcessingStatus = IdempotencyStatus.Completed,
                CreatedAt = Now.AddDays(-10),
                LastSeenAt = Now.AddDays(-10),
                RetentionDeadline = Now.AddDays(-1),
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Run sweep.
        RetentionSweepResult result;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var handler = new RetentionSweepHandler(ctx);
            result = await handler.SweepIdempotencyRecordsAsync(
                tenantId, Now, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, result.Pruned);
        Assert.True(result.Skipped >= 1);

        // Record must still be present.
        await using (var readCtx = fixture.CreateContext(tenantId))
        {
            var remaining = await readCtx.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.IdempotencyKey == iKey,
                    TestContext.Current.CancellationToken);
            Assert.NotNull(remaining);
        }

        // NO audit event should have been written for this key.
        await using (var readCtx = fixture.CreateContext(tenantId))
        {
            var auditRow = await readCtx.AuditEvents
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    e => e.EventType == "privacy.retention.idempotency_pruned"
                         && e.SubjectRef == iKey,
                    TestContext.Current.CancellationToken);
            Assert.Null(auditRow);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (3) Not-yet-due: future retention_deadline → record not pruned.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Sweep_DoesNotPrune_WhenDeadlineHasNotElapsed()
    {
        var tenantId = TestIds.New("t6-due");
        var iKey = TestIds.New("ikey");

        // Seed terminal op so the only guard is the future deadline.
        string operationId;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(
                ctx, tenantId, OperationStatus.Booked, TestContext.Current.CancellationToken);
        }

        // Insert idempotency_record with retention_deadline in the FUTURE.
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            ctx.IdempotencyRecords.Add(new IdempotencyRecord
            {
                TenantId = tenantId,
                IdempotencyKey = iKey,
                OperationType = Domain.Generated.OperationTypeEnum.Create,
                RequestHash = "h",
                OperationId = operationId,
                ProcessingStatus = IdempotencyStatus.Completed,
                CreatedAt = Now.AddDays(-5),
                LastSeenAt = Now.AddDays(-5),
                RetentionDeadline = Now.AddDays(1),
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Run sweep.
        RetentionSweepResult result;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var handler = new RetentionSweepHandler(ctx);
            result = await handler.SweepIdempotencyRecordsAsync(
                tenantId, Now, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, result.Pruned);

        // Record must still be present.
        await using (var readCtx = fixture.CreateContext(tenantId))
        {
            var remaining = await readCtx.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.IdempotencyKey == iKey,
                    TestContext.Current.CancellationToken);
            Assert.NotNull(remaining);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (4) Cross-tenant safety (P1 regression): a context scoped to tenant-A holds a
    //     protected (non-terminal) record; sweeping under a DIFFERENT tenantId must
    //     touch nothing of tenant-A's — never load it via the ambient filter and
    //     mis-classify it as an orphan to prune.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Sweep_WithMismatchedContextTenant_DoesNotTouchContextTenantRecords()
    {
        var protectedTenant = TestIds.New("t6-prot");
        var otherTenant = TestIds.New("t6-other");
        var iKey = TestIds.New("ikey");

        // Seed protectedTenant: a NON-terminal op + a past-deadline record (would be SKIPPED
        // if swept correctly under protectedTenant; would be wrongly pruned by the old bug
        // when swept under a different tenantId via this context).
        string operationId;
        await using (var ctx = fixture.CreateContext(protectedTenant))
        {
            operationId = await TestData.SeedOperationAsync(
                ctx, protectedTenant, OperationStatus.ManualReview, TestContext.Current.CancellationToken);
            ctx.IdempotencyRecords.Add(new IdempotencyRecord
            {
                TenantId = protectedTenant,
                IdempotencyKey = iKey,
                OperationType = Domain.Generated.OperationTypeEnum.Create,
                RequestHash = "h",
                OperationId = operationId,
                ProcessingStatus = IdempotencyStatus.Completed,
                CreatedAt = Now.AddDays(-10),
                LastSeenAt = Now.AddDays(-10),
                RetentionDeadline = Now.AddDays(-1),
            });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Run the sweep for a DIFFERENT tenant, but using a context scoped to protectedTenant.
        RetentionSweepResult result;
        await using (var ctx = fixture.CreateContext(protectedTenant))
        {
            var handler = new RetentionSweepHandler(ctx);
            result = await handler.SweepIdempotencyRecordsAsync(
                otherTenant, Now, TestContext.Current.CancellationToken);
        }

        // The sweep targeted otherTenant (which has nothing): no prune, no skip.
        Assert.Equal(0, result.Pruned);
        Assert.Equal(0, result.Skipped);

        // protectedTenant's record must be untouched.
        await using (var readCtx = fixture.CreateContext(protectedTenant))
        {
            var remaining = await readCtx.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    r => r.IdempotencyKey == iKey,
                    TestContext.Current.CancellationToken);
            Assert.NotNull(remaining);
        }

        // No audit row should have been written under EITHER tenant for this key.
        await using (var readCtx = fixture.CreateContext(otherTenant))
        {
            var auditUnderOther = await readCtx.AuditEvents
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    e => e.SubjectRef == iKey,
                    TestContext.Current.CancellationToken);
            Assert.Null(auditUnderOther);
        }
    }
}
