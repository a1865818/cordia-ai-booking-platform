#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Extensions;
using Cordia.Infrastructure.Messaging;
using Cordia.Infrastructure.Services;
using Cordia.Tests.IntegrationTests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cordia.Tests.IntegrationTests.Messaging;

/// <summary>
/// Integration tests for <see cref="MissingNotificationAuditHostedService"/>
/// (spec 10 §25.4 / FM#12 / Inv-14, T5b).
///
/// Each test uses a unique tenant so assertions are scoped and the shared test DB
/// cannot interfere (other terminal ops missing notifications are fine).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class MissingNotificationAuditTests(PostgresFixture fixture)
{
    private static ServiceProvider BuildContainer()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:CordiaDBConnection"] = PostgresFixture.TestConnStr,
                ["Messaging:Publisher:Enabled"] = "false",
                ["Messaging:ScheduledJobPoller:Enabled"] = "false",
                ["Messaging:StepSweeper:Enabled"] = "false",
                ["Messaging:Sla:Enabled"] = "false",
                ["Messaging:NotificationReconcile:Enabled"] = "false",
                ["Messaging:MissingNotificationAudit:Enabled"] = "false", // prevent background loop
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);
        return services.BuildServiceProvider();
    }

    private static MissingNotificationAuditHostedService BuildAuditor(
        IServiceProvider sp,
        int terminalAgeSeconds = 30,
        int batchSize = 50)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        return new MissingNotificationAuditHostedService(
            scopeFactory,
            Options.Create(new MessagingOptions
            {
                MissingNotificationAudit = new MissingNotificationAuditOptions
                {
                    Enabled = true,
                    TerminalAgeSeconds = terminalAgeSeconds,
                    BatchSize = batchSize,
                },
            }),
            NullLogger<MissingNotificationAuditHostedService>.Instance);
    }

    /// <summary>
    /// Seeds a terminal BookingOperation (Booked) with NO notification row and an explicit
    /// UpdatedAt = now-5min. Neutralizes pre-existing terminal ops that lack notifications
    /// (they sort ahead of our row and could fill the batch before our op is reached).
    /// Then calls <see cref="MissingNotificationAuditHostedService.AuditOnceAsync"/> via the
    /// domain seam directly — same pattern as <c>NotificationReconcileTests</c> — and asserts
    /// an OPEN <c>operation_work_items</c> row with <c>reason="missing_notification"</c> exists.
    /// </summary>
    [Fact]
    public async Task TerminalOp_NoNotification_CreatesOpenWorkItem()
    {
        var tenantId = $"t5b-miss-{Guid.NewGuid():N}";

        // ── Neutralize pre-existing terminal ops that lack accepted notifications. ─────
        // The shared test DB accumulates terminal BookingOperations from prior runs.
        // Many have UpdatedAt = 0001-01-01 (EF default) which sorts ahead of our
        // now-5min row; with a 50-row batch they would starve our op.
        // We neutralize by parking them in a synthetic "already-audited" state: create
        // a placeholder open work item for each un-audited terminal op.  But that is
        // expensive.  Instead, use the same approach as ReconciliationTests: bulk-update
        // their UpdatedAt to now so they fall outside the TerminalAgeSeconds=30 window.
        await using (var cleanupCtx = fixture.CreateContext("t5b-miss-cleanup"))
        {
            await cleanupCtx.BookingOperations
                .IgnoreQueryFilters()
                .Where(op =>
                    op.UpdatedAt < DateTimeOffset.UtcNow.AddSeconds(-30) &&
                    (op.Status == OperationStatus.Booked ||
                     op.Status == OperationStatus.Rejected ||
                     op.Status == OperationStatus.Expired ||
                     op.Status == OperationStatus.Waitlisted ||
                     op.Status == OperationStatus.FailedFinal))
                .ExecuteUpdateAsync(b => b.SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));
        }

        // ── Seed a terminal BookingOperation with no notification row. ─────────────────
        string operationId;
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Booked);

            // Set UpdatedAt explicitly to 5 min ago so the threshold filter matches.
            await seedCtx.BookingOperations
                .IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.OperationId == operationId)
                .ExecuteUpdateAsync(b => b.SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow.AddMinutes(-5)));
        }

        // ── Build and run one audit tick. ─────────────────────────────────────────────
        await using var sp = BuildContainer();
        var auditor = BuildAuditor(sp, terminalAgeSeconds: 30, batchSize: 50);
        await auditor.AuditOnceAsync(CancellationToken.None);

        // ── Assert: an open work item with reason="missing_notification" exists. ───────
        // Scope the assertion to our unique tenant + operationId.
        await using var assertCtx = fixture.CreateContext(tenantId);
        var workItem = await assertCtx.OperationWorkItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(wi =>
                wi.TenantId == tenantId &&
                wi.OperationId == operationId &&
                wi.Reason == "missing_notification" &&
                wi.State == "open");

        Assert.NotNull(workItem);
    }

    /// <summary>
    /// Seeds a terminal BookingOperation (Booked) WITH an Accepted notification row.
    /// Calls the <see cref="IMissingNotificationAuditor"/> seam directly (per-op, not a
    /// full sweep tick) so the test is isolated from shared-DB noise.
    /// Asserts NO <c>missing_notification</c> work item is created (defensive re-check
    /// short-circuits when a notification is already accepted).
    /// </summary>
    [Fact]
    public async Task TerminalOp_WithAcceptedNotification_NoWorkItemCreated()
    {
        var tenantId = $"t5b-ok-{Guid.NewGuid():N}";

        // ── Seed a terminal BookingOperation + an accepted notification. ───────────────
        string operationId;
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Booked);

            // Insert an Accepted notification for this operation.
            var notifId = $"notif-{Guid.NewGuid():N}";
            seedCtx.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                NotificationId = notifId,
                OperationId = operationId,
                EventType = NotificationEvent.BookingConfirmed,
                RecipientRef = "recip-test",
                TemplateVersion = "v1",
                MaterialRevision = 1,
                State = NotificationState.Accepted,
                Channel = NotificationChannel.Sms,
                Priority = MessagePriority.High,
                ProviderIdempotencyKey = $"pkey-{Guid.NewGuid():N}",
                AttemptCount = 1,
            });
            await seedCtx.SaveChangesAsync();
        }

        // ── Invoke the auditor seam directly for our specific op. ─────────────────────
        // Mirrors how ReconcileNotificationsHostedService tests call INotificationReconciler
        // directly — surfaces exceptions and avoids shared-DB batch-noise.
        await using var sp = BuildContainer();
        await using (var scope = sp.CreateAsyncScope())
        {
            var tenantCtx = scope.ServiceProvider.GetRequiredService<TenantContext>();
            tenantCtx.TenantId = tenantId;
            var auditor = scope.ServiceProvider.GetRequiredService<IMissingNotificationAuditor>();
            await auditor.AuditAsync(tenantId, operationId, CancellationToken.None);
        }

        // ── Assert: NO missing_notification work item for this operation. ─────────────
        await using var assertCtx = fixture.CreateContext(tenantId);
        var workItem = await assertCtx.OperationWorkItems
            .AsNoTracking()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(wi =>
                wi.TenantId == tenantId &&
                wi.OperationId == operationId &&
                wi.Reason == "missing_notification");

        Assert.Null(workItem);
    }

    /// <summary>
    /// Proves the sweep query's <c>NOT EXISTS</c> exclusion directly (T5b review finding #1).
    /// Under one tenant, seed two terminal ops: one WITHOUT a notification and one WITH an
    /// Accepted notification. <see cref="INotificationRepository.ListTerminalOperationsMissingAcceptedNotificationAsync"/>
    /// must return the former and EXCLUDE the latter. This asserts the QUERY (not the handler's
    /// defensive re-check), so a regression dropping the inner <c>IgnoreQueryFilters()</c> —
    /// which would collapse the accepted set and surface covered ops as "missing" — fails here.
    /// </summary>
    [Fact]
    public async Task Sweep_ExcludesOpsWithAcceptedNotification()
    {
        var tenantId = $"t5b-excl-{Guid.NewGuid():N}";

        // Neutralize pre-existing terminal ops so only our two fall inside the now-30s window.
        await using (var cleanupCtx = fixture.CreateContext("t5b-excl-cleanup"))
        {
            await cleanupCtx.BookingOperations
                .IgnoreQueryFilters()
                .Where(op =>
                    op.UpdatedAt < DateTimeOffset.UtcNow.AddSeconds(-30) &&
                    (op.Status == OperationStatus.Booked ||
                     op.Status == OperationStatus.Rejected ||
                     op.Status == OperationStatus.Expired ||
                     op.Status == OperationStatus.Waitlisted ||
                     op.Status == OperationStatus.FailedFinal))
                .ExecuteUpdateAsync(b => b.SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow));
        }

        string opMissing, opCovered;
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            opMissing = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Booked);
            opCovered = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Booked);

            // opCovered gets an Accepted notification → must be excluded by the NOT EXISTS.
            seedCtx.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                NotificationId = $"notif-{Guid.NewGuid():N}",
                OperationId = opCovered,
                EventType = NotificationEvent.BookingConfirmed,
                RecipientRef = "recip-test",
                TemplateVersion = "v1",
                MaterialRevision = 1,
                State = NotificationState.Accepted,
                Channel = NotificationChannel.Sms,
                Priority = MessagePriority.High,
                ProviderIdempotencyKey = $"pkey-{Guid.NewGuid():N}",
                AttemptCount = 1,
            });
            await seedCtx.SaveChangesAsync();

            // Both ops eligible: UpdatedAt = now-5min (< the now-30s threshold).
            await seedCtx.BookingOperations
                .IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && (x.OperationId == opMissing || x.OperationId == opCovered))
                .ExecuteUpdateAsync(b => b.SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow.AddMinutes(-5)));
        }

        await using var sp = BuildContainer();
        await using var scope = sp.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<INotificationRepository>();

        var missing = await repo.ListTerminalOperationsMissingAcceptedNotificationAsync(
            DateTimeOffset.UtcNow.AddSeconds(-30), limit: 500, CancellationToken.None);

        var forTenant = missing.Where(m => m.TenantId == tenantId).Select(m => m.OperationId).ToHashSet();
        Assert.Contains(opMissing, forTenant);        // no notification → surfaced
        Assert.DoesNotContain(opCovered, forTenant);  // accepted notification → excluded by NOT EXISTS
    }
}
