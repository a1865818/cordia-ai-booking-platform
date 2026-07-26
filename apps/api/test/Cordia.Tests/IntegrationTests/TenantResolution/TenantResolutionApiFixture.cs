using Alba;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cordia.Tests.IntegrationTests.TenantResolution;

/// <summary>
/// Shared xUnit fixture for the spec-15 channel-gateway ingress tests (Phase 4). Mirrors
/// <c>AcceptanceApiFixture</c>: ensures the shared test database exists, boots an in-process Alba host
/// configured identically to the real API (Test environment, MigrateOnStartup), and exposes a
/// per-tenant <see cref="ApplicationDbContext"/> factory for direct row assertions.
///
/// <para>The Test-environment <c>appsettings.Test.json</c> already provisions the spec-15 JWS signing
/// keys (<c>Secrets:TenantContextSigningKeys</c>), so the <c>ITenantContextSigner</c> singleton
/// constructs and the gateway can mint/verify tokens.</para>
/// </summary>
public class TenantResolutionApiFixture : IAsyncLifetime
{
    private const string MaintenanceConnStr =
        "Host=localhost;Port=5432;Database=postgres;Username=cordia;Password=localdev";

    // Same test database as the other integration-fixture families.
    public const string TestConnStr =
        "Host=localhost;Port=5432;Database=Cordia_API_Test;Username=cordia;Password=localdev";

    /// <summary>The in-process Alba host; available after <see cref="InitializeAsync"/> completes.</summary>
    public IAlbaHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await EnsureTestDatabaseExistsAsync();

        Host = await AlbaHost.For<Program>(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("ConnectionStrings:CordiaDBConnection", TestConnStr);
            builder.UseSetting("DatabaseBootstrap:MigrateOnStartup", "true");
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null)
            await Host.DisposeAsync();
    }

    /// <summary>
    /// Creates an <see cref="ApplicationDbContext"/> scoped to <paramref name="tenantId"/> against the
    /// shared test database. Used by tests to seed endpoints and assert on persisted rows directly.
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
                    .MapEnum<ReleaseReason>("release_reason_enum"))
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

[CollectionDefinition("TenantResolutionApi")]
public class TenantResolutionApiCollection : ICollectionFixture<TenantResolutionApiFixture> { }
