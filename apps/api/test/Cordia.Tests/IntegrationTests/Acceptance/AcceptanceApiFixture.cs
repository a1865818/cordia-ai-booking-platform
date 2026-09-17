using Alba;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cordia.Tests.IntegrationTests.Acceptance;

/// <summary>
/// Shared xUnit fixture: ensures the test database exists, runs EF migrations, and
/// boots an in-process Alba host configured identically to the real API (except for
/// the connection string and migration-on-startup flag).
///
/// Lifecycle:
///   1. EnsureTestDatabaseExistsAsync — idempotently creates Cordia_API_Test.
///   2. AlbaHost.For&lt;Program&gt; — builds the ASP.NET host, MigrateOnStartup=true
///      applies any outstanding migrations inside Program.cs.
///   3. DisposeAsync — disposes the Alba host (shuts down the in-process server).
/// </summary>
public class AcceptanceApiFixture : IAsyncLifetime
{
    private const string MaintenanceConnStr = Persistence.TestDatabase.MaintenanceConnStr;

    // Both fixture families share one test database; the value lives in TestDatabase so the
    // port and credentials cannot drift between fixtures.
    public const string TestConnStr = Persistence.TestDatabase.ConnStr;

    /// <summary>The in-process Alba host; available after <see cref="InitializeAsync"/> completes.</summary>
    public IAlbaHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        // Steps 1 and 2 run under one lock: this fixture, TenantResolutionApiFixture and
        // PostgresFixture all bring the schema up, and xUnit starts their collections in parallel.
        // Against an empty database that races and the losers die on 42P07 — see TestSchemaLock.
        await using (await Persistence.TestSchemaLock.AcquireAsync())
        {
            // Step 1: ensure the database exists before the host tries to migrate it.
            await EnsureTestDatabaseExistsAsync();

            // Step 2: boot the Alba host.
            // UseSetting overrides appsettings.json values inside the test process.
            Host = await AlbaHost.For<Program>(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("ConnectionStrings:CordiaDBConnection", TestConnStr);
                // Run EF migrations on startup so the schema is always current.
                builder.UseSetting("DatabaseBootstrap:MigrateOnStartup", "true");
            });
        }

        // Both collections share one database, so either may be the first to run. Settling here
        // too means the backlog is cleared whichever order xUnit picks (see StaleFixtureLitter).
        await Persistence.StaleFixtureLitter.SettleAsync(TestConnStr);
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
            await Host.DisposeAsync();
    }

    /// <summary>
    /// Creates an <see cref="ApplicationDbContext"/> scoped to <paramref name="tenantId"/>
    /// against the shared test database.  Used by tests to assert on persisted rows directly.
    /// </summary>
    public ApplicationDbContext CreateContext(string tenantId)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder
            .UseNpgsql(
                TestConnStr,
                o => o
                    .MapEnum<ConsentState>("consent_state")
                    .MapEnum<TenantChannel>("channel_enum")
                    .MapEnum<OperationTypeEnum>("operation_type_enum")
                    .MapEnum<OperationStatus>("operation_status_enum")
                    .MapEnum<BookingLifecycleStatus>("booking_lifecycle_status_enum")
                    .MapEnum<IdempotencyStatus>("idempotency_status_enum")
                    .MapEnum<VerificationLevel>("verification_level_enum")
                    .MapEnum<WorkflowStepStatus>("workflow_step_status_enum")
                    .MapEnum<AttemptOutcome>("attempt_outcome_enum")
                    .MapEnum<RetryClassification>("retry_classification_enum")
                    .MapEnum<InboxOutcome>("inbox_outcome_enum")
                    .MapEnum<ScheduledJobStatus>("scheduled_job_status_enum")
                    .MapEnum<DlqReplayState>("dlq_replay_state_enum")
                    .MapEnum<NotificationState>("notification_state_enum")
                    .MapEnum<NotificationEvent>("notification_event_enum")
                    .MapEnum<NotificationChannel>("notification_channel_enum")
                    .MapEnum<MessagePriority>("message_priority_enum")
                    .MapEnum<VerificationActionEnum>("verification_action_enum")
                    .MapEnum<VerificationRequestChannel>("verification_request_channel_enum")
                    .MapEnum<CatalogStatus>("catalog_status_enum")
                    .MapEnum<DepositStatus>("deposit_status_enum")
                    .MapEnum<WaitlistOfferStatus>("waitlist_offer_status_enum")
                    .MapEnum<PreorderSagaState>("preorder_saga_state_enum")
                    .MapEnum<SignalType>("signal_type_enum")
                    .MapEnum<EndpointStatus>("endpoint_status_enum")
                    .MapEnum<HoldStatus>("hold_status_enum")
                    .MapEnum<ReleaseReason>("release_reason_enum")
                    .MapEnum<AttributedSource>("attributed_source_enum")
                    .MapEnum<AttendanceStatus>("attendance_status_enum")
                    .MapEnum<CampaignStatus>("campaign_status_enum")
                    .MapEnum<KnowledgeStatus>("knowledge_status_enum"))
            .UseSnakeCaseNamingConvention();

        return new ApplicationDbContext(
            optionsBuilder.Options,
            string.IsNullOrEmpty(tenantId) ? new TenantContext() : new TenantContext(tenantId));
    }

    private static async Task EnsureTestDatabaseExistsAsync()
    {
        await using var conn = new NpgsqlConnection(MaintenanceConnStr);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM pg_database WHERE datname = 'Cordia_API_Test'";
        var exists = await cmd.ExecuteScalarAsync();

        if (exists is null)
        {
            cmd.CommandText = "CREATE DATABASE \"Cordia_API_Test\"";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}

[CollectionDefinition("AcceptanceApi")]
public class AcceptanceApiCollection : ICollectionFixture<AcceptanceApiFixture> { }
