using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cordia.Tests.IntegrationTests.Persistence;

/// <summary>
/// Shared xUnit fixture: ensures the test database exists, runs migrations once,
/// and exposes a factory to create per-tenant <see cref="ApplicationDbContext"/> instances.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    private const string MaintenanceConnStr = TestDatabase.MaintenanceConnStr;

    /// <summary>
    /// Kept as a forwarding constant rather than moved: 26 test files reference
    /// <c>PostgresFixture.TestConnStr</c> by name. The value lives in <see cref="TestDatabase"/>.
    /// </summary>
    public const string TestConnStr = TestDatabase.ConnStr;

    public async ValueTask InitializeAsync()
    {
        // Serialised against the two Alba fixtures, which migrate the same database from their own
        // collections in parallel. See TestSchemaLock for the 42P07 race this closes.
        await using (await TestSchemaLock.AcquireAsync())
        {
            await EnsureTestDatabaseExistsAsync();

            // Run migrations against the test database.
            await using var ctx = CreateContext("fixture-init");
            await ctx.Database.MigrateAsync();
        }

        // Settle what previous runs left behind, or the cross-tenant sweepers' bounded batches
        // slowly fill with history until a test's own row no longer fits (see StaleFixtureLitter).
        await StaleFixtureLitter.SettleAsync(TestConnStr);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Creates an <see cref="ApplicationDbContext"/> scoped to <paramref name="tenantId"/>.</summary>
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

[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture> { }
