using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Cordia.Infrastructure.EntityFramework;

/// <summary>
/// Enables <c>dotnet ef migrations add</c> without a running web host.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    private const string DevConnectionString =
        "Host=localhost;Port=5432;Database=Cordia_API;Username=cordia;Password=localdev";

    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder
            .UseNpgsql(
                DevConnectionString,
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
            new TenantContext { TenantId = "design-time" });
    }
}
