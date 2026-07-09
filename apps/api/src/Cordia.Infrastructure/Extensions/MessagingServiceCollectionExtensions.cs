using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cordia.Infrastructure.Extensions;

/// <summary>Registers the Infrastructure messaging services (transport seam, §3, OD-1).</summary>
internal static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IMessageTransport"/> and <see cref="IQueueConsumerDispatcher"/> as
    /// Scoped services. Both live within the outbox publisher's per-tick scope (T3), avoiding
    /// captive-dependency issues. <see cref="IQueueConsumerDispatcher"/> is implemented by
    /// <c>MediatRQueueConsumerDispatcher</c> in Application (registered there alongside
    /// <c>ConsumerRegistry</c>).
    ///
    /// Also registers:
    ///   - <see cref="MessagingOptions"/> bound from the "Messaging" config section (T3).
    ///   - <see cref="OutboxPublisherHostedService"/> as a hosted service (T3); disabled via
    ///     <c>Messaging:Publisher:Enabled=false</c> in appsettings.Test.json so existing Alba
    ///     integration tests are unaffected.
    /// </summary>
    internal static IServiceCollection AddMessaging(this IServiceCollection services)
    {
        services.AddScoped<IMessageTransport, PostgresMessageTransport>();

        // T7: dead-letter handler (Scoped — uses IDeadLetterRepository which is Scoped).
        // T11: also register via IDeadLetterHandler (domain interface) so Application layer can inject it.
        services.AddScoped<DeadLetterHandler>();
        services.AddScoped<IDeadLetterHandler>(sp => sp.GetRequiredService<DeadLetterHandler>());

        // T3: options + publisher hosted service.
        services.AddOptions<MessagingOptions>()
            .BindConfiguration(MessagingOptions.SectionName);

        services.AddHostedService<OutboxPublisherHostedService>();

        // T5: scheduled-job poller; disabled via Messaging:ScheduledJobPoller:Enabled=false
        // in appsettings.Test.json so existing Alba integration tests are unaffected.
        services.AddHostedService<ScheduledJobPollerHostedService>();

        // T6: step sweeper; disabled via Messaging:StepSweeper:Enabled=false in
        // appsettings.Test.json so existing Alba integration tests are unaffected.
        services.AddHostedService<StepSweeperHostedService>();

        // T9: SLA evaluator; disabled via Messaging:Sla:Enabled=false in
        // appsettings.Test.json so existing Alba integration tests are unaffected.
        services.AddHostedService<SlaEvaluatorHostedService>();

        // T4 (spec 10): notification reconciler; disabled via Messaging:NotificationReconcile:Enabled=false
        // in appsettings.Test.json so existing Alba integration tests are unaffected.
        services.AddHostedService<ReconcileNotificationsHostedService>();

        // T5b (spec 10): missing-terminal-notification auditor; disabled via
        // Messaging:MissingNotificationAudit:Enabled=false in appsettings.Test.json so existing
        // Alba integration tests are unaffected.
        services.AddHostedService<MissingNotificationAuditHostedService>();

        // Spec 11 §5.3 (divergence #3): pre-order saga recovery sweeper backstop; disabled via
        // Messaging:PreorderRecovery:Enabled=false in appsettings.Test.json so existing Alba
        // integration tests are unaffected.
        services.AddHostedService<PreorderRecoverySweeperHostedService>();

        return services;
    }
}
