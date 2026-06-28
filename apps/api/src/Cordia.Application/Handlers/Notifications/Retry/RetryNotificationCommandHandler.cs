using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Notifications.Retry;

/// <summary>
/// Handles durable retry of a <c>FailedTransient</c> notification (spec 10 FM#6, T5a).
///
/// <para><b>Runs inside the inbox transaction.</b> Do NOT open a new <c>IUnitOfWork</c>
/// transaction — all writes share the ambient scoped DbContext.</para>
///
/// <para><b>Inv-8:</b> this handler NEVER writes to <c>booking_operations</c>.</para>
///
/// <para><b>Version tracking:</b> after <c>MarkTransientRetryAsync</c> the row is at
/// <c>FailedTransient(v≥2)</c>. <c>TryTransitionStateAsync(FailedTransient→Sending)</c>
/// advances it one more version. Terminal settles (Accepted/FailedPermanent/etc.) use
/// the version after the Sending transition, which this handler captures as
/// <c>sendingVersion</c>.</para>
/// </summary>
public sealed class RetryNotificationCommandHandler(
    INotificationRepository repo,
    IEnumerable<INotificationProviderAdapter> adapters,
    IAuditEventRepository auditRepo,
    NotificationRetryScheduler retryScheduler,
    ILogger<RetryNotificationCommandHandler> logger)
    : IRequestHandler<RetryNotificationCommand, Unit>
{
    public async Task<Unit> Handle(RetryNotificationCommand command, CancellationToken ct)
    {
        var tenantId = command.TenantId;
        var notificationId = command.NotificationId;

        // 1. Load row.
        var n = await repo.GetAsync(tenantId, notificationId, ct);
        if (n is null)
        {
            logger.LogWarning(
                "RetryNotification: notification_id={NotificationId} not found for tenant {TenantId}; ack-and-drop.",
                notificationId, tenantId);
            return Unit.Value;
        }

        // 2. Idempotent no-op: only FailedTransient rows need retry.
        if (n.State != NotificationState.FailedTransient)
        {
            logger.LogDebug(
                "RetryNotification: notification_id={NotificationId} is in state {State}; expected FailedTransient — no-op.",
                notificationId, n.State);
            return Unit.Value;
        }

        // 3. Eligibility check: honour the scheduled next_eligible_at.
        if (n.NextEligibleAt is { } eligible && eligible > DateTimeOffset.UtcNow)
        {
            logger.LogDebug(
                "RetryNotification: notification_id={NotificationId} next_eligible_at={NextEligibleAt} is in the future; skipping.",
                notificationId, n.NextEligibleAt);
            return Unit.Value;
        }

        // 4. Claim: FailedTransient → Sending.  The version at this point is n.Version.
        var rows = await repo.TryTransitionStateAsync(
            tenantId, notificationId,
            n.Version,
            NotificationState.FailedTransient,
            NotificationState.Sending,
            ct);

        if (rows == 0)
        {
            logger.LogWarning(
                "RetryNotification: lost race claiming FailedTransient→Sending for notification_id={NotificationId}.",
                notificationId);
            return Unit.Value;
        }

        // After the version-token update the row version advanced by 1.
        var sendingVersion = n.Version + 1;

        // 5. Select adapter.
        var adapter = adapters.FirstOrDefault(a => a.Channel == n.Channel);
        if (adapter is null)
        {
            var noAdapterRows = await repo.MarkFailedAsync(
                tenantId, notificationId,
                sendingVersion,
                NotificationState.Sending,
                NotificationState.FailedPermanent,
                "no_adapter_for_channel",
                ct);

            if (noAdapterRows == 0)
                logger.LogWarning(
                    "RetryNotification: lost race settling Sending→FailedPermanent (no_adapter) for {NotificationId}.",
                    notificationId);

            logger.LogWarning(
                "RetryNotification: no adapter for channel {Channel}; settled FailedPermanent for {NotificationId}.",
                n.Channel, notificationId);

            await EmitAuditAsync(n, "notification.failed_permanent",
                new { notification_id = notificationId, failure_category = "no_adapter_for_channel", channel = n.Channel.ToString() },
                ct);

            return Unit.Value;
        }

        // 6. Send.
        var rendered = new RenderedMessage(
            TemplateVersion: n.TemplateVersion,
            Channel: n.Channel,
            Locale: "en",
            Body: string.Empty,
            MinimizedRecipient: n.RecipientRef);

        var result = await adapter.SendAsync(n.ProviderIdempotencyKey!, rendered, ct);

        switch (result.Outcome)
        {
            case NotificationSendOutcome.Accepted:
                var acceptedRows = await repo.MarkAcceptedAsync(
                    tenantId, notificationId,
                    sendingVersion,
                    NotificationState.Sending,
                    providerRef: result.ProviderMessageId,
                    ct);

                if (acceptedRows == 0)
                    logger.LogWarning(
                        "RetryNotification: lost race settling Sending→Accepted for {NotificationId}.",
                        notificationId);

                await EmitAuditAsync(n, "notification.accepted",
                    new { notification_id = notificationId, channel = n.Channel.ToString(), provider_ref = result.ProviderMessageId },
                    ct);
                break;

            case NotificationSendOutcome.FailedTransient:
                // Re-reload to get the current version after the Sending transition.
                var reloaded = await repo.GetAsync(tenantId, notificationId, ct);
                if (reloaded is not null)
                {
                    await retryScheduler.HandleTransientAsync(
                        reloaded,
                        expectedVersion: sendingVersion,
                        result.FailureCategory,
                        ct);
                }
                else
                {
                    logger.LogWarning(
                        "RetryNotification: could not reload notification_id={NotificationId} for HandleTransientAsync.",
                        notificationId);
                }
                break;

            case NotificationSendOutcome.FailedPermanent:
                var permanentRows = await repo.MarkFailedAsync(
                    tenantId, notificationId,
                    sendingVersion,
                    NotificationState.Sending,
                    NotificationState.FailedPermanent,
                    result.FailureCategory ?? "permanent",
                    ct);

                if (permanentRows == 0)
                    logger.LogWarning(
                        "RetryNotification: lost race settling Sending→FailedPermanent for {NotificationId}.",
                        notificationId);

                await EmitAuditAsync(n, "notification.failed_permanent",
                    new { notification_id = notificationId, failure_category = result.FailureCategory ?? "permanent" },
                    ct);
                break;

            case NotificationSendOutcome.InvalidRecipient:
                var invalidRows = await repo.MarkFailedAsync(
                    tenantId, notificationId,
                    sendingVersion,
                    NotificationState.Sending,
                    NotificationState.InvalidRecipient,
                    "provider_invalid",
                    ct);

                if (invalidRows == 0)
                    logger.LogWarning(
                        "RetryNotification: lost race settling Sending→InvalidRecipient for {NotificationId}.",
                        notificationId);

                await EmitAuditAsync(n, "notification.invalid_recipient",
                    new { notification_id = notificationId, failure_category = "provider_invalid" },
                    ct);
                break;
        }

        return Unit.Value;
    }

    private Task EmitAuditAsync(Notification n, string eventType, object metadata, CancellationToken ct)
        => auditRepo.AddAsync(new AuditEvent
        {
            TenantId = n.TenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = eventType,
            Actor = "system",
            SubjectRef = n.NotificationId,
            CorrelationId = n.NotificationId,
            OperationId = n.OperationId,
            Metadata = JsonSerializer.Serialize(metadata),
            OccurredAt = DateTimeOffset.UtcNow,
        }, ct);
}
