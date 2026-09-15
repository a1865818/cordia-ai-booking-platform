#pragma warning disable xUnit1051
using Cordia.Application.Extensions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
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
/// Integration tests for <see cref="ReconcileNotificationsHostedService"/>
/// (spec 10 §12.8 / §13.6 capability ladder, T4).
///
/// Seeds a parent BookingOperation + a <c>notifications</c> row directly in state <c>sending</c>
/// with a stable <c>provider_idempotency_key</c>, <c>provider_ref=null</c>, <c>channel=sms</c>,
/// <c>updated_at</c> older than the stuck threshold, version=1.  Calls
/// <see cref="ReconcileNotificationsHostedService.ReconcileOnceAsync"/> once and asserts the row
/// transitions to <c>accepted</c> with a non-null <c>provider_ref</c>.
///
/// The stub SmsNotificationAdapter has both <c>SupportsLookup=true</c> and
/// <c>SupportsSendIdempotency=true</c>, so the service settles via the lookup branch (§13.6(a)).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Postgres")]
public class NotificationReconcileTests(PostgresFixture fixture)
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
                // StuckThresholdSeconds=0 ensures any row is eligible immediately.
                ["Messaging:NotificationReconcile:StuckThresholdSeconds"] = "0",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(config);
        services.AddApplication();
        services.AddInfrastructure(config);
        return services.BuildServiceProvider();
    }

    private static ReconcileNotificationsHostedService BuildReconciler(IServiceProvider sp)
    {
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var options = sp.GetRequiredService<IOptions<MessagingOptions>>();
        return new ReconcileNotificationsHostedService(
            scopeFactory, options, NullLogger<ReconcileNotificationsHostedService>.Instance);
    }

    /// <summary>
    /// Seeds a Sending notification row older than the stuck threshold; drives the
    /// ReconcileNotificationsHostedService directly by calling the domain seam; asserts
    /// the row is now Accepted with a non-null provider_ref.
    ///
    /// Uses the MediatR seam directly (via INotificationReconciler) rather than going
    /// through ReconcileOnceAsync, because the test DB may contain stuck rows from
    /// prior test runs (with -infinity updated_at and no idempotency key) that would
    /// cause the hosted-service's per-row exception handler to silently swallow errors
    /// and return reconciled=0.  The seam call lets exceptions propagate so failures
    /// are visible in the test output.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_StuckSendingRow_SmsAdapter_TransitionsToAccepted()
    {
        var tenantId = $"t4-rec-{Guid.NewGuid():N}";
        var notifId = $"notif-{Guid.NewGuid():N}";
        var providerKey = $"pkey-{Guid.NewGuid():N}";

        // ── Seed a BookingOperation (FK requirement) and a stuck-Sending notification. ──────────
        string operationId;
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Booked);

            // Insert directly in state Sending.
            // EF `Add` omits `updated_at` (its value equals the configured store default), so the
            // DB stores `0001-01-01`, which is always `< olderThan` and thus eligible. Production
            // rows are inserted by `TryInsertAsync` with a real `now()` timestamp — this
            // `0001-01-01` value is a test-seed artifact only.
            seedCtx.Notifications.Add(new Notification
            {
                TenantId = tenantId,
                NotificationId = notifId,
                OperationId = operationId,
                EventType = NotificationEvent.BookingConfirmed,
                RecipientRef = "recip-reconcile",
                TemplateVersion = "v1",
                MaterialRevision = 1,
                State = NotificationState.Sending,
                Channel = NotificationChannel.Sms,
                Priority = MessagePriority.High,
                ProviderIdempotencyKey = providerKey,
                ProviderRef = null, // not yet accepted (crash-window simulation)
                AttemptCount = 1,
            });
            await seedCtx.SaveChangesAsync();
        }

        // ── Invoke the reconcile seam directly for our specific row. ─────────────────────────────
        // This mirrors how ReconcileNotificationsHostedService works per-row, but surfaces
        // exceptions instead of catching them — suitable for a test assertion.
        await using var sp = BuildContainer();
        await using (var scope = sp.CreateAsyncScope())
        {
            var tenantCtx = scope.ServiceProvider.GetRequiredService<TenantContext>();
            tenantCtx.SetTenant(tenantId);
            var reconciler = scope.ServiceProvider.GetRequiredService<INotificationReconciler>();
            await reconciler.ReconcileAsync(tenantId, notifId, CancellationToken.None);
        }

        // ── Assert: the specific row is now Accepted with a non-null provider_ref. ──────────────
        // Use a unique tenantId per test + filter by notifId to avoid interference from other rows.
        await using var assertCtx = fixture.CreateContext(tenantId);
        var n = await assertCtx.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.NotificationId == notifId);

        Assert.NotNull(n);
        Assert.Equal(NotificationState.Accepted, n!.State);
        Assert.NotNull(n.ProviderRef);
    }

    /// <summary>
    /// Exercises the full hosted-service tick: <see cref="ReconcileNotificationsHostedService.ReconcileOnceAsync"/>
    /// drives the list-phase (<c>ListStuckSendingAsync</c> query + <c>StuckThresholdSeconds</c> threshold
    /// math) and the per-row scope/tenant-context loop.
    ///
    /// Seeds a single Sending row with an EXPLICIT <c>UpdatedAt = now-5min</c> so the test truly
    /// exercises the <c>updated_at &lt; olderThan</c> threshold filter (StuckThresholdSeconds=30 ⇒
    /// a now-5min row is eligible) rather than relying on the 0001-01-01 seed artifact.
    ///
    /// The stub Sms adapter has lookup=true, so the row settles via the lookup branch (§13.6(a)).
    /// Asserts only the specific row's final state — the shared test DB may contain other stuck rows.
    /// </summary>
    [Fact]
    public async Task ReconcileOnceAsync_HostTick_StuckSendingRow_TransitionsToAccepted()
    {
        var tenantId = $"t10-recon-tick-{Guid.NewGuid():N}";
        var notifId = $"notif-{Guid.NewGuid():N}";
        var providerKey = $"pkey-{Guid.NewGuid():N}";

        // ── Neutralize pre-existing stuck-Sending rows from prior test runs. ────────────────────
        // The shared test DB accumulates Sending rows (0001-01-01 updated_at) that sort ahead of
        // our now-5min row; with a 50-row batch they could starve our row out of a single tick.
        // Park them in FailedPermanent so the batch is free for our seeded row (mirrors
        // RejectionTests.NeutralizeExistingUnpublishedAsync).
        await using (var cleanupCtx = fixture.CreateContext("t10-recon-tick-cleanup"))
        {
            await cleanupCtx.Notifications
                .IgnoreQueryFilters()
                .Where(x => x.State == NotificationState.Sending)
                .ExecuteUpdateAsync(b => b.SetProperty(x => x.State, NotificationState.FailedPermanent));
        }

        // ── Seed a BookingOperation (FK requirement) and a stuck-Sending notification. ──────────
        string operationId;
        await using (var seedCtx = fixture.CreateContext(tenantId))
        {
            operationId = await TestData.SeedOperationAsync(seedCtx, tenantId, OperationStatus.Booked);

            var n = new Notification
            {
                TenantId = tenantId,
                NotificationId = notifId,
                OperationId = operationId,
                EventType = NotificationEvent.BookingConfirmed,
                RecipientRef = "recip-reconcile",
                TemplateVersion = "v1",
                MaterialRevision = 1,
                State = NotificationState.Sending,
                Channel = NotificationChannel.Sms,
                Priority = MessagePriority.High,
                ProviderIdempotencyKey = providerKey,
                ProviderRef = null, // not yet accepted (crash-window simulation)
                AttemptCount = 1,
                Version = 1,
            };
            seedCtx.Notifications.Add(n);
            await seedCtx.SaveChangesAsync();

            // Set an EXPLICIT updated_at = now-5min so ReconcileOnceAsync's threshold filter
            // (updated_at < now - 30s) actually matches this row on its own merit.
            await seedCtx.Notifications
                .IgnoreQueryFilters()
                .Where(x => x.TenantId == tenantId && x.NotificationId == notifId)
                .ExecuteUpdateAsync(b => b.SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow.AddMinutes(-5)));
        }

        // ── Build the host directly (mirror RejectionTests.BuildPublisher) and run one tick. ───
        await using var sp = BuildContainer();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var host = new ReconcileNotificationsHostedService(
            scopeFactory,
            Options.Create(new MessagingOptions
            {
                NotificationReconcile = new NotificationReconcileOptions
                {
                    Enabled = true,
                    StuckThresholdSeconds = 30,
                    BatchSize = 50,
                },
            }),
            NullLogger<ReconcileNotificationsHostedService>.Instance);

        await host.ReconcileOnceAsync(CancellationToken.None);

        // ── Assert: our specific row is now Accepted with a non-null provider_ref (via lookup). ──
        await using var assertCtx = fixture.CreateContext(tenantId);
        var settled = await assertCtx.Notifications
            .AsNoTracking()
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.NotificationId == notifId);

        Assert.NotNull(settled);
        Assert.Equal(NotificationState.Accepted, settled!.State);
        Assert.NotNull(settled.ProviderRef);
    }
}
