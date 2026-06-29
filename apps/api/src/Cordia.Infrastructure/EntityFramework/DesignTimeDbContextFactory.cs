using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cordia.Infrastructure.EntityFramework;

/// <summary>
/// Enables <c>dotnet ef migrations add</c> without a running web host.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    private const string LocalConnectionString =
        "Host=localhost;Port=5432;Database=Cordia_API;Username=cordia;Password=localdev";

    // Allow `dotnet ef` to target any database (e.g. a cloud Postgres) without editing this file:
    // set ConnectionStrings__CordiaDBConnection in the environment. Falls back to local dev.
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__CordiaDBConnection") is { Length: > 0 } env
            ? env
            : LocalConnectionString;

    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder
            .UseNpgsql(
                ConnectionString,
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
            new TenantContext { TenantId = "design-time" });
    }
}
