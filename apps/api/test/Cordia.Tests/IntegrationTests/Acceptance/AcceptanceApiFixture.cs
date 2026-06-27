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
    private const string MaintenanceConnStr =
        "Host=localhost;Port=5432;Database=postgres;Username=cordia;Password=localdev";

    // Re-use the same connection string as the Postgres persistence fixture so
    // both fixture families share one test database.
    public const string TestConnStr =
        "Host=localhost;Port=5432;Database=Cordia_API_Test;Username=cordia;Password=localdev";

    /// <summary>The in-process Alba host; available after <see cref="InitializeAsync"/> completes.</summary>
    public IAlbaHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
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
                    .MapEnum<VerificationActionEnum>("verification_action_enum")
                    .MapEnum<CatalogStatus>("catalog_status_enum")
                    .MapEnum<DepositStatus>("deposit_status_enum")
                    .MapEnum<WaitlistOfferStatus>("waitlist_offer_status_enum")
                    .MapEnum<PreorderSagaState>("preorder_saga_state_enum")
                    .MapEnum<SignalType>("signal_type_enum")
                    .MapEnum<EndpointStatus>("endpoint_status_enum"))
            .UseSnakeCaseNamingConvention();

        return new ApplicationDbContext(
            optionsBuilder.Options,
            new TenantContext { TenantId = tenantId });
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
