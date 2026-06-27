using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Services;
using Cordia.Infrastructure.Services.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cordia.Infrastructure.Extensions;

/// <summary>Registers the Infrastructure layer (EF Core, repositories).</summary>
public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("CordiaDBConnection");

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options
                .UseNpgsql(
                    connectionString,
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
        });

        // The DbContext consumes ITenantContext (read-only). A future ingress middleware
        // (tenant resolution, spec 15) resolves the concrete TenantContext from the same
        // scope and sets TenantId; both registrations return the SAME scoped instance so
        // the setter is observed by the DbContext's global query filter.
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TenantContext>(sp => (TenantContext)sp.GetRequiredService<ITenantContext>());

        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();
        services.AddScoped<IBookingOperationRepository, BookingOperationRepository>();
        services.AddScoped<IBookingIntentRepository, BookingIntentRepository>();
        services.AddScoped<IWorkflowStepRepository, WorkflowStepRepository>();
        services.AddScoped<IWorkflowAttemptRepository, WorkflowAttemptRepository>();
        services.AddScoped<IOperationWorkItemRepository, OperationWorkItemRepository>();
        services.AddScoped<IOutboxRepository, OutboxRepository>();
        services.AddScoped<IInboxRepository, InboxRepository>();
        services.AddScoped<IDeadLetterRepository, DeadLetterRepository>();
        services.AddScoped<IScheduledJobRepository, ScheduledJobRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IVerificationGrantRepository, VerificationGrantRepository>();
        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<IDepositSessionRepository, DepositSessionRepository>();
        services.AddScoped<IWaitlistOfferRepository, WaitlistOfferRepository>();
        services.AddScoped<ITenantChannelEndpointRepository, TenantChannelEndpointRepository>();

        services.AddScoped<RetentionSweepHandler>();

        // Pure, stateless, thread-safe → Singleton (spec 04 §5).
        services.AddSingleton<ICanonicalRequestHasher, CanonicalRequestHasher>();

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddMessaging();

        return services;
    }
}
