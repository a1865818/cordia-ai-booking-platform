#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken
using System.Text.Json;
using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Application.Handlers.BookingFulfillment.ReserveCapacity;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Capacity;

/// <summary>
/// Integration tests for <see cref="ReserveCapacityCommandHandler"/> (spec 07 T5).
///
/// Each test uses a unique tenantId for row isolation. Handlers are constructed directly
/// over <see cref="PostgresFixture.CreateContext"/> with real repos and a real
/// <see cref="FirstPartyCapacityReservation"/> to verify the full wiring from handler
/// down to the conditional UPDATE.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class ReserveCapacityHandlerTests(PostgresFixture fixture)
{
    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static CapacityOptions DefaultCapacityOptions => new() { HoldTtl = TimeSpan.FromMinutes(30) };

    /// <summary>Builds the full handler wired to the given DbContext.</summary>
    private static ReserveCapacityCommandHandler BuildHandler(ApplicationDbContext ctx)
    {
        var slotRepo = new CapacitySlotRepository(ctx);
        var holdRepo = new CapacityHoldRepository(ctx);
        var intentRepo = new BookingIntentRepository(ctx);
        var opRepo = new BookingOperationRepository(ctx);
        var stepRepo = new WorkflowStepRepository(ctx);
        var outboxRepo = new OutboxRepository(ctx);
        var auditRepo = new AuditEventRepository(ctx);
        var scheduledJobRepo = new ScheduledJobRepository(ctx);
        var uow = new UnitOfWork(ctx);
        var capacityReservation = new FirstPartyCapacityReservation(ctx, holdRepo);
        var expiryScheduler = new CapacityExpiryScheduler(scheduledJobRepo);
        var options = Options.Create(DefaultCapacityOptions);

        return new ReserveCapacityCommandHandler(
            opRepo, stepRepo, capacityReservation, slotRepo, intentRepo,
            expiryScheduler, outboxRepo, auditRepo, uow, options);
    }

    /// <summary>
    /// Seeds a capacity slot (total=<paramref name="total"/>), intent + revision with a canonical
    /// payload that maps to the slot key, and a booking operation in ReservingCapacity.
    /// Returns (slotId, operationId).
    /// </summary>
    private async Task<(string slotId, string operationId)> SeedTrackedOpAsync(
        string tenantId,
        string resourceId,
        DateTimeOffset requestedAt,
        int total,
        int held = 0)
    {
        var slotId = $"{resourceId}|{requestedAt:O}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        var canonicalPayload = JsonSerializer.Serialize(new
        {
            party_size = 1,
            resource_id = resourceId,
            requested_at = requestedAt.ToString("O"),
        });

        await using var ctx = fixture.CreateContext(tenantId);

        ctx.CapacitySlots.Add(new CapacitySlot
        {
            TenantId = tenantId,
            SlotId = slotId,
            ResourceId = resourceId,
            StartsAt = requestedAt,
            TotalCapacity = total,
            HeldCapacity = held,
            BookedCapacity = 0,
            Version = 0,
        });

        var intent = new BookingIntent
        {
            TenantId = tenantId,
            IntentId = intentId,
            OperationType = OperationTypeEnum.Create,
        };
        var revision = new BookingIntentRevision
        {
            TenantId = tenantId,
            IntentId = intentId,
            Revision = 1,
            CanonicalPayload = canonicalPayload,
            PayloadHash = "h",
            SchemaVersion = "1.0",
            TenantPolicyVersion = "1.0",
            Intent = intent,
        };
        var operation = new BookingOperation
        {
            TenantId = tenantId,
            OperationId = operationId,
            IntentId = intentId,
            IntentRevision = 1,
            OperationType = OperationTypeEnum.Create,
            Status = OperationStatus.ReservingCapacity,
            Version = 0,
        };

        ctx.BookingIntents.Add(intent);
        ctx.BookingIntentRevisions.Add(revision);
        ctx.BookingOperations.Add(operation);
        await ctx.SaveChangesAsync();

        return (slotId, operationId);
    }

    // ── Test 1: Two ops race for the last unit — exactly one Held, other AlternativesRequired ─

    /// <summary>
    /// §27.3: two operations target the same slot (total=1). Handler runs for op A then op B
    /// (each in its own scope/context). Asserts:
    ///   - Exactly one op ends Held with an active hold row.
    ///   - The other op ends AlternativesRequired.
    ///   - Slot held_capacity == 1 (never overcommitted).
    ///   - A booking.scheduled.hold_expiry.v1 scheduled_job row exists for the Held op.
    /// </summary>
    [Fact]
    public async Task TwoOpsRaceForLastUnit_ExactlyOneHeld_OtherAlternativesRequired_SlotNotOvercommitted()
    {
        var tenantId = $"t7-handler-race-{Guid.NewGuid():N}";
        var resourceId = $"res-{Guid.NewGuid():N}";
        var requestedAt = DateTimeOffset.UtcNow.AddHours(1);

        // Seed slot (total=1) and two ops that both point to the same slot key.
        var (slotId, opA) = await SeedTrackedOpAsync(tenantId, resourceId, requestedAt, total: 1);

        // Seed op B separately (same slot key).
        string opB;
        {
            var intentId = $"int-{Guid.NewGuid():N}";
            opB = $"op-{Guid.NewGuid():N}";
            var canonicalPayload = JsonSerializer.Serialize(new
            {
                party_size = 1,
                resource_id = resourceId,
                starts_at = requestedAt.ToString("O"),
            });

            await using var seedCtx = fixture.CreateContext(tenantId);
            var intent = new BookingIntent
            {
                TenantId = tenantId,
                IntentId = intentId,
                OperationType = OperationTypeEnum.Create,
            };
            var revision = new BookingIntentRevision
            {
                TenantId = tenantId,
                IntentId = intentId,
                Revision = 1,
                CanonicalPayload = canonicalPayload,
                PayloadHash = "h",
                SchemaVersion = "1.0",
                TenantPolicyVersion = "1.0",
                Intent = intent,
            };
            var operation = new BookingOperation
            {
                TenantId = tenantId,
                OperationId = opB,
                IntentId = intentId,
                IntentRevision = 1,
                OperationType = OperationTypeEnum.Create,
                Status = OperationStatus.ReservingCapacity,
                Version = 0,
            };
            seedCtx.BookingIntents.Add(intent);
            seedCtx.BookingIntentRevisions.Add(revision);
            seedCtx.BookingOperations.Add(operation);
            await seedCtx.SaveChangesAsync();
        }

        // Run handler for op A in its own context (scope A).
        StepOutcome resultA;
        await using (var ctxA = fixture.CreateContext(tenantId))
        {
            var handlerA = BuildHandler(ctxA);
            resultA = await handlerA.Handle(new ReserveCapacityCommand(tenantId, opA), default);
        }

        // Run handler for op B in its own context (scope B).
        StepOutcome resultB;
        await using (var ctxB = fixture.CreateContext(tenantId))
        {
            var handlerB = BuildHandler(ctxB);
            resultB = await handlerB.Handle(new ReserveCapacityCommand(tenantId, opB), default);
        }

        // Assert: outcomes are one Held (Continue) + one AlternativesRequired (Wait).
        // Note: which op wins the race is deterministic here (sequential execution), so A wins.
        Assert.Equal(StepOutcome.Continue, resultA);
        Assert.Equal(StepOutcome.Wait, resultB);

        // Assert own op statuses.
        await using var verifyCtx = fixture.CreateContext(tenantId);

        var opARow = await verifyCtx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == opA);
        var opBRow = await verifyCtx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == opB);

        Assert.Equal(OperationStatus.Held, opARow.Status);
        Assert.Equal(OperationStatus.AlternativesRequired, opBRow.Status);

        // Assert: slot held_capacity == 1 (no overcommit).
        var slot = await verifyCtx.CapacitySlots.AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(s => s.SlotId == slotId);
        Assert.Equal(1, slot.HeldCapacity);
        Assert.Equal(0, slot.BookedCapacity);
        Assert.True(slot.HeldCapacity + slot.BookedCapacity <= slot.TotalCapacity);

        // Assert: exactly one active hold row for op A.
        var holds = await verifyCtx.CapacityHolds.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId && (h.OperationId == opA || h.OperationId == opB))
            .ToListAsync();
        Assert.Single(holds);
        Assert.Equal(opA, holds[0].OperationId);
        Assert.Equal(HoldStatus.Active, holds[0].Status);
        Assert.Equal(slotId, holds[0].SlotId);

        // Assert: a hold-expiry scheduled job exists for op A.
        var expiryJob = await verifyCtx.ScheduledJobs.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(j =>
                j.TenantId == tenantId &&
                j.OperationId == opA &&
                j.JobType == "booking.scheduled.hold_expiry.v1");
        Assert.NotNull(expiryJob);
        Assert.Equal("booking.scheduled", expiryJob!.DestinationQueue);
        Assert.Equal(ScheduledJobStatus.Pending, expiryJob.Status);
        Assert.Contains(holds[0].HoldId, expiryJob.Payload);

        // Assert: no expiry job for op B (it didn't get a hold).
        var expiryJobB = await verifyCtx.ScheduledJobs.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(j =>
                j.TenantId == tenantId &&
                j.OperationId == opB &&
                j.JobType == "booking.scheduled.hold_expiry.v1");
        Assert.Null(expiryJobB);
    }

    // ── Test 2: Non-tracked op (no slot) → Held, no hold, no expiry job ─────────────────────

    /// <summary>
    /// When the canonical payload has no resource dimension (or no slot row exists for the
    /// derived key), the handler transitions to Held without a hold or expiry job (mode=live).
    /// </summary>
    [Fact]
    public async Task NonTrackedOp_TransitionsToHeld_NoHoldRow_NoExpiryJob()
    {
        var tenantId = $"t7-handler-untracked-{Guid.NewGuid():N}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        // Seed op with empty canonical payload (no resource dimension → not tracked).
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            var intent = new BookingIntent
            {
                TenantId = tenantId,
                IntentId = intentId,
                OperationType = OperationTypeEnum.Create,
            };
            var revision = new BookingIntentRevision
            {
                TenantId = tenantId,
                IntentId = intentId,
                Revision = 1,
                CanonicalPayload = "{}",
                PayloadHash = "h",
                SchemaVersion = "1.0",
                TenantPolicyVersion = "1.0",
                Intent = intent,
            };
            var operation = new BookingOperation
            {
                TenantId = tenantId,
                OperationId = operationId,
                IntentId = intentId,
                IntentRevision = 1,
                OperationType = OperationTypeEnum.Create,
                Status = OperationStatus.ReservingCapacity,
                Version = 0,
            };
            seedCtx.BookingIntents.Add(intent);
            seedCtx.BookingIntentRevisions.Add(revision);
            seedCtx.BookingOperations.Add(operation);
            await seedCtx.SaveChangesAsync();
        }

        StepOutcome result;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var handler = BuildHandler(ctx);
            result = await handler.Handle(new ReserveCapacityCommand(tenantId, operationId), default);
        }

        Assert.Equal(StepOutcome.Continue, result);

        await using var verifyCtx = fixture.CreateContext(tenantId);

        // Op must be Held.
        var op = await verifyCtx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(OperationStatus.Held, op.Status);

        // No holds (non-tracked → no durable hold).
        var holds = await verifyCtx.CapacityHolds.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId && h.OperationId == operationId)
            .ToListAsync();
        Assert.Empty(holds);

        // No expiry jobs (no hold to expire).
        var jobs = await verifyCtx.ScheduledJobs.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(j => j.TenantId == tenantId && j.OperationId == operationId
                        && j.JobType == "booking.scheduled.hold_expiry.v1")
            .ToListAsync();
        Assert.Empty(jobs);
    }

    // ── Test 3: Tracked op with starts_at in payload → Held with hold row (FIX 1) ───────────

    /// <summary>
    /// FIX 1: a canonical payload that uses starts_at (not requested_at) still reaches the
    /// tracked path — a hold row is created and an expiry job is enqueued.
    /// </summary>
    [Fact]
    public async Task TrackedOp_StartsAtPayload_ReachesHeldWithHoldRow()
    {
        var tenantId = $"t7-handler-startsat-{Guid.NewGuid():N}";
        var resourceId = $"res-{Guid.NewGuid():N}";
        var startsAt = DateTimeOffset.UtcNow.AddHours(3);

        // Seed slot + op with a starts_at payload.
        var slotId = $"{resourceId}|{startsAt:O}";
        var intentId = $"int-{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";

        var canonicalPayload = JsonSerializer.Serialize(new
        {
            party_size = 1,
            resource_id = resourceId,
            starts_at = startsAt.ToString("O"),
            // NO requested_at — canonical agent format
        });

        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            seedCtx.CapacitySlots.Add(new CapacitySlot
            {
                TenantId = tenantId,
                SlotId = slotId,
                ResourceId = resourceId,
                StartsAt = startsAt,
                TotalCapacity = 1,
                HeldCapacity = 0,
                BookedCapacity = 0,
                Version = 0,
            });

            var intent = new BookingIntent
            {
                TenantId = tenantId,
                IntentId = intentId,
                OperationType = OperationTypeEnum.Create,
            };
            var revision = new BookingIntentRevision
            {
                TenantId = tenantId,
                IntentId = intentId,
                Revision = 1,
                CanonicalPayload = canonicalPayload,
                PayloadHash = "h",
                SchemaVersion = "1.0",
                TenantPolicyVersion = "1.0",
                Intent = intent,
            };
            var operation = new BookingOperation
            {
                TenantId = tenantId,
                OperationId = operationId,
                IntentId = intentId,
                IntentRevision = 1,
                OperationType = OperationTypeEnum.Create,
                Status = OperationStatus.ReservingCapacity,
                Version = 0,
            };
            seedCtx.BookingIntents.Add(intent);
            seedCtx.BookingIntentRevisions.Add(revision);
            seedCtx.BookingOperations.Add(operation);
            await seedCtx.SaveChangesAsync();
        }

        StepOutcome result;
        await using (var ctx = fixture.CreateContext(tenantId))
        {
            var handler = BuildHandler(ctx);
            result = await handler.Handle(new ReserveCapacityCommand(tenantId, operationId), default);
        }

        Assert.Equal(StepOutcome.Continue, result);

        await using var verifyCtx = fixture.CreateContext(tenantId);

        // op → Held.
        var op = await verifyCtx.BookingOperations.AsNoTracking()
            .FirstAsync(o => o.TenantId == tenantId && o.OperationId == operationId);
        Assert.Equal(OperationStatus.Held, op.Status);

        // An active hold row must exist (tracked path fired).
        var holds = await verifyCtx.CapacityHolds.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(h => h.TenantId == tenantId && h.OperationId == operationId)
            .ToListAsync();
        Assert.Single(holds);
        Assert.Equal(HoldStatus.Active, holds[0].Status);
        Assert.Equal(slotId, holds[0].SlotId);

        // An expiry job was enqueued.
        var expiryJob = await verifyCtx.ScheduledJobs.AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(j =>
                j.TenantId == tenantId &&
                j.OperationId == operationId &&
                j.JobType == "booking.scheduled.hold_expiry.v1");
        Assert.NotNull(expiryJob);
        Assert.Contains(holds[0].HoldId, expiryJob!.Payload);

        // Slot held_capacity == 1.
        var slot = await verifyCtx.CapacitySlots.AsNoTracking()
            .IgnoreQueryFilters()
            .SingleAsync(s => s.SlotId == slotId);
        Assert.Equal(1, slot.HeldCapacity);
        Assert.Equal(0, slot.BookedCapacity);
    }

    // ── Test 4: Idempotent expiry enqueue — two ScheduleAsync for same hold → exactly one job (FIX 6) ──

    /// <summary>
    /// FIX 6: calling CapacityExpiryScheduler.ScheduleAsync twice for the same holdId
    /// produces exactly one scheduled_job row (deterministic jobId "sj_holdexp_{holdId}").
    /// </summary>
    [Fact]
    public async Task ExpiryScheduler_SameHold_IdempotentEnqueue_ExactlyOneJobRow()
    {
        var tenantId = $"t7-handler-idem-{Guid.NewGuid():N}";
        var holdId = $"hold_{Guid.NewGuid():N}";
        var operationId = $"op-{Guid.NewGuid():N}";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(45);

        // Call ScheduleAsync twice for the same holdId in two separate contexts.
        await using (var ctx1 = fixture.CreateContext(tenantId))
        {
            var jobRepo = new ScheduledJobRepository(ctx1);
            var scheduler = new CapacityExpiryScheduler(jobRepo);

            // First call — must insert.
            await using var tx1 = await ctx1.Database.BeginTransactionAsync();
            await scheduler.ScheduleAsync(tenantId, operationId, holdId, expiresAt, default);
            await tx1.CommitAsync();
        }

        await using (var ctx2 = fixture.CreateContext(tenantId))
        {
            var jobRepo = new ScheduledJobRepository(ctx2);
            var scheduler = new CapacityExpiryScheduler(jobRepo);

            // Second call with same holdId — must be a no-op (idempotent).
            await using var tx2 = await ctx2.Database.BeginTransactionAsync();
            await scheduler.ScheduleAsync(tenantId, operationId, holdId, expiresAt, default);
            await tx2.CommitAsync();
        }

        // Verify: exactly ONE scheduled_job row for this hold.
        await using var verifyCtx = fixture.CreateContext(tenantId);
        var expectedJobId = $"sj_holdexp_{holdId}";
        var jobCount = await verifyCtx.ScheduledJobs.AsNoTracking()
            .IgnoreQueryFilters()
            .CountAsync(j => j.TenantId == tenantId && j.JobId == expectedJobId);
        Assert.Equal(1, jobCount);
    }
}
