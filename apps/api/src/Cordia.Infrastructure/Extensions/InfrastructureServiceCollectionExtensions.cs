using Cordia.Domain.Enums;
using Cordia.Domain.Fulfillment;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Cordia.Infrastructure.Providers;
using Cordia.Infrastructure.Repositories;
using Cordia.Infrastructure.Retry;
using Cordia.Infrastructure.Security;
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
        });

        // The DbContext consumes ITenantContext (read-only). A future ingress middleware
        // (tenant resolution, spec 15) resolves the concrete TenantContext from the same
        // scope and sets TenantId; both registrations return the SAME scoped instance so
        // the setter is observed by the DbContext's global query filter.
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddScoped<TenantContext>(sp => (TenantContext)sp.GetRequiredService<ITenantContext>());

        services.AddScoped<IIdempotencyRepository, IdempotencyRepository>();
        services.AddScoped<IBookingOperationRepository, BookingOperationRepository>();
        services.AddScoped<IBookingRepository, BookingRepository>();
        services.AddScoped<IBookingReadRepository, BookingReadRepository>();
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
        services.AddScoped<IVerificationChallengeRepository, VerificationChallengeRepository>();
        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<IDepositSessionRepository, DepositSessionRepository>();
        services.AddScoped<IWaitlistOfferRepository, WaitlistOfferRepository>();
        services.AddScoped<IWaitlistEnrollmentRepository, WaitlistEnrollmentRepository>();
        services.AddScoped<IVendorCatalogRepository, VendorCatalogRepository>();
        services.AddScoped<ITenantChannelEndpointRepository, TenantChannelEndpointRepository>();
        services.AddScoped<IPreorderSagaRepository, PreorderSagaRepository>();

        // Spec 07 T3 — capacity slot + hold repos + first-party reservation service.
        services.AddScoped<ICapacitySlotRepository, CapacitySlotRepository>();
        services.AddScoped<ICapacityHoldRepository, CapacityHoldRepository>();
        services.AddScoped<ICapacityReservation, FirstPartyCapacityReservation>();

        // Spec 07 T4 — primitive resolver + stub external adapter (spec 07 §13 open decision:
        // no real provider configured yet; resolver defaults every triple to FirstParty).
        services.AddSingleton<ICapacityPrimitiveResolver, CapacityPrimitiveResolver>();
        services.AddSingleton<IExternalInventoryAdapter, StubExternalInventoryAdapter>();

        // Spec 07 T5 — capacity options bound from "Capacity" config section.
        services.AddOptions<CapacityOptions>()
            .BindConfiguration(CapacityOptions.SectionName);

        services.AddScoped<RetentionSweepHandler>();
        // Spec 14 §24.3 — cross-tenant retention driver consumed by RetentionSweepHostedService.
        services.AddScoped<IRetentionSweeper, RetentionSweeper>();

        // Spec 14 — consent vertical: session consent updates + jurisdiction policy.
        services.AddScoped<IConversationSessionRepository, ConversationSessionRepository>();
        services.AddSingleton<IJurisdictionPolicyProvider, JurisdictionPolicyProvider>();

        // Spec 14 §24.4 — server-side secret custody; values never serialized or logged.
        services.AddSingleton<ISecretStore, ConfigurationSecretStore>();

        // Pure, stateless, thread-safe → Singleton (spec 04 §5).
        services.AddSingleton<ICanonicalRequestHasher, CanonicalRequestHasher>();

        // Spec 06 T2 reliability building blocks — all singletons (stateless or own thread-safe state).
        services.AddSingleton<RetryClassifier>();
        services.AddSingleton<IRetryPolicy, FullJitterRetryPolicy>();
        services.AddSingleton<IProviderCircuitBreaker, InMemoryProviderCircuitBreaker>();
        services.AddSingleton<IBookingProviderAdapter, StubBookingProviderAdapter>();

        // Spec 06 T7 deposit + verification seams — singletons (stateless stubs; spec 09 / spec 08 replace).
        services.AddSingleton<IDepositPolicy, NoDepositPolicy>();
        services.AddSingleton<IVerificationService, NoUserActionRequiredService>();

        // Spec 11 — menu ingestion ports (deterministic stubs; real ClamAV / LangGraph extractor
        // swap in later behind these ports). Stateless → singletons.
        services.AddSingleton<IArtifactScanner, StubArtifactScanner>();
        services.AddSingleton<IIsolatedMenuExtractor, StubIsolatedMenuExtractor>();

        // Spec 11 — pre-order item-reservation port (deterministic stub; real POS adapter swaps in
        // later behind this port). Stateless → singleton. Payment is no longer a saga concern: the
        // whole booking (incl. spec-09 payment) is delegated to the spec-06 engine (§5.3, divergence #2).
        services.AddSingleton<IPreorderItemReservation, StubPreorderItemReservation>();

        // Spec 06 T11 validation policy seam — singleton (always-pass stub; spec 04 real engine replaces).
        services.AddSingleton<IValidationPolicy, AlwaysValidPolicy>();

        // Spec 10 T2 — notification provider adapters (one per channel; injected as IEnumerable<INotificationProviderAdapter>).
        services.AddScoped<INotificationProviderAdapter, SmsNotificationAdapter>();
        services.AddScoped<INotificationProviderAdapter, EmailNotificationAdapter>();
        services.AddScoped<INotificationProviderAdapter, ChannelCallbackAdapter>();

        // Spec 10 T2 — notification support seams (stubs; real store/consent/minimization is §31).
        services.AddScoped<INotificationTemplateResolver, NotificationTemplateResolver>();
        services.AddScoped<IConsentGate, ConsentGate>();
        services.AddScoped<INotificationRecipientResolver, NotificationRecipientResolver>();

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddMessaging();

        return services;
    }
}
