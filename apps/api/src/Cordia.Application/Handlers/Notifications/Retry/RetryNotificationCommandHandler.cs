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
    INotificationRecipientResolver recipientResolver,
    INotificationRepository repo,
    IEnumerable<INotificationProviderAdapter> adapters,
    IAuditEventRepository auditRepo,
    NotificationRetryScheduler retryScheduler,
    INotificationTemplateResolver templateResolver,
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

            if (noAdapterRows > 0)
            {
                await EmitAuditAsync(n, "notification.failed_permanent",
                    new { notification_id = notificationId, failure_category = "no_adapter_for_channel", channel = n.Channel.ToString() },
                    ct);
            }

            return Unit.Value;
        }

        // 6. Resolve template + send (F10: render a real body on retry resend).
        var template = await templateResolver.ResolveAsync(n.EventType, n.Channel, "en", ct);
        // Re-resolve the destination rather than reading it off the row: it is deliberately not
        // persisted, so a retry must go through the same single resolver the first attempt used.
        // Without this a retry would render a message with nowhere to deliver.
        //
        // A null OperationId means "non-operation message" (spec 10), not "no recipient". Every
        // waitlist offer/expiry row has one, so gating on it alone sent each of them straight to
        // the no-destination guard below and settled InvalidRecipient — terminal, and wrong: the
        // enrollment id on correlation_id still reaches the guest. Same fallback as the reconcile
        // lane, and it fails closed for any row whose correlation is not an enrollment.
        var retryRecipient = await ResolveDestinationAsync(n, ct);
        var rendered = new RenderedMessage(
            TemplateVersion: n.TemplateVersion,
            Channel: n.Channel,
            Locale: "en",
            Body: template.BodyTemplate,
            MinimizedRecipient: n.RecipientRef,
            Destination: retryRecipient?.Destination);

        NotificationSendResult result;
        if (retryRecipient?.Destination is null)
        {
            // Nowhere to deliver. The FIRST send settles this as invalid_recipient; without the
            // same guard here a transient failure followed by a retry would sail past it and
            // settle as delivered — reporting a delivery that never happened (Inv-16).
            // Settled through the existing switch rather than thrown: this handler runs inside
            // the inbox transaction, and throwing for an expected terminal outcome rolls back
            // admission and causes poison redelivery.
            logger.LogInformation(
                "RetryNotification: no destination for {NotificationId}; settling InvalidRecipient.",
                notificationId);
            result = new NotificationSendResult(
                NotificationSendOutcome.InvalidRecipient,
                ProviderMessageId: null,
                FailureCategory: "no_destination");
        }
        else
        {
            try
            {
                result = await adapter.SendAsync(n.ProviderIdempotencyKey!, rendered, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // F9: guard against adapter exceptions — treat as transient failure so the inbox
                // transaction COMMITS (row settles to FailedTransient + durable retry scheduled)
                // rather than rolling back and causing a poison redelivery loop.
                logger.LogWarning(ex,
                    "RetryNotification: adapter.SendAsync threw for {NotificationId}; treating as transient.",
                    notificationId);
                var reloadedOnEx = await repo.GetAsync(tenantId, notificationId, ct);
                if (reloadedOnEx is not null)
                {
                    await retryScheduler.HandleTransientAsync(reloadedOnEx, expectedVersion: sendingVersion, "send_exception", ct);
                }
                else
                {
                    logger.LogWarning(
                        "RetryNotification: could not reload notification_id={NotificationId} after send exception.",
                        notificationId);
                }
                return Unit.Value;
            }
        }

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

                if (acceptedRows > 0)
                {
                    await EmitAuditAsync(n, "notification.accepted",
                        new { notification_id = notificationId, channel = n.Channel.ToString(), provider_ref = result.ProviderMessageId },
                        ct);
                }
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

                if (permanentRows > 0)
                {
                    await EmitAuditAsync(n, "notification.failed_permanent",
                        new { notification_id = notificationId, failure_category = result.FailureCategory ?? "permanent" },
                        ct);
                }
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

                if (invalidRows > 0)
                {
                    await EmitAuditAsync(n, "notification.invalid_recipient",
                        new { notification_id = notificationId, failure_category = "provider_invalid" },
                        ct);
                }
                break;
        }

        return Unit.Value;
    }

    /// <summary>
    /// The destination for a retry, from whichever link the row actually carries: the booking
    /// operation when there is one, else the enrollment id on <c>correlation_id</c> for a waitlist
    /// offer/expiry (whose <c>op_wl_*</c> id is deliberately not persisted — spec 10).
    ///
    /// <para>Gated on the EVENT TYPE as well as the null operation, for the reason spelled out on
    /// the reconcile lane's copy: <c>correlation_id</c> is a generic column, and without the gate
    /// the next <c>RequiresOperation: false</c> registration would silently resolve a destination
    /// from an unrelated enrollment.</para>
    /// </summary>
    private async Task<ResolvedRecipient?> ResolveDestinationAsync(Notification n, CancellationToken ct)
    {
        if (n.OperationId is not null)
            return await recipientResolver.ResolveAsync(n.TenantId, n.OperationId, ct);

        if (n.EventType is not (NotificationEvent.WaitlistOffer or NotificationEvent.WaitlistOfferExpired))
            return null;

        return string.IsNullOrWhiteSpace(n.CorrelationId)
            ? null
            : await recipientResolver.ResolveByEnrollmentAsync(n.TenantId, n.CorrelationId, ct);
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
