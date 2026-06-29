using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Notifications.Reconcile;

/// <summary>
/// Handles §12.8 ambiguous-send recovery for one notification row stuck in <c>Sending</c>
/// (spec 10 §6.2, §13.6 capability ladder).
///
/// <para><b>Algorithm (§13.6 ladder):</b></para>
/// <list type="number">
///   <item>Load row; guard state == Sending.</item>
///   <item>Resolve adapter; no adapter → settle FailedPermanent.</item>
///   <item>(a) Lookup-by-key (if SupportsLookup): found+Accepted → settle Accepted, NO resend.</item>
///   <item>(b/c) Resend under the SAME stable provider_idempotency_key (provider dedups if
///       SupportsSendIdempotency). If neither capability flag is true (FM#5), emit
///       <c>notification.duplicate_risk</c> audit (measured residual risk).</item>
/// </list>
///
/// <para><b>Version guards:</b> all settles pass <c>n.Version</c> + expectedState <c>Sending</c>.
/// If 0 rows affected, someone else reconciled first — return <c>NotSending</c>, never force.</para>
///
/// <para><b>Inv-8:</b> this handler NEVER writes to <c>booking_operations</c>.</para>
///
/// <para>Invoked from the hosted service (not a queue consumer), so it owns its own txn via
/// <c>IUnitOfWork.ExecuteInTransactionAsync</c>.</para>
/// </summary>
public sealed class ReconcileNotificationCommandHandler(
    INotificationRepository repo,
    IEnumerable<INotificationProviderAdapter> adapters,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow,
    NotificationRetryScheduler retryScheduler,
    INotificationTemplateResolver templateResolver,
    ILogger<ReconcileNotificationCommandHandler> logger)
    : IRequestHandler<ReconcileNotificationCommand, ReconcileOutcome>
{
    public async Task<ReconcileOutcome> Handle(
        ReconcileNotificationCommand command,
        CancellationToken ct)
    {
        ReconcileOutcome outcome = ReconcileOutcome.NotFound;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            // 1. Load the row.
            var n = await repo.GetAsync(command.TenantId, command.NotificationId, ct2);
            if (n is null)
            {
                outcome = ReconcileOutcome.NotFound;
                return;
            }

            // 2. Guard: only Sending rows need reconciliation.
            if (n.State != NotificationState.Sending)
            {
                outcome = ReconcileOutcome.NotSending;
                return;
            }

            // 3. Resolve adapter. If none exists for this channel, settle FailedPermanent.
            var adapter = adapters.FirstOrDefault(a => a.Channel == n.Channel);
            if (adapter is null)
            {
                var noAdapterRows = await repo.MarkFailedAsync(
                    command.TenantId, command.NotificationId,
                    n.Version, NotificationState.Sending,
                    NotificationState.FailedPermanent,
                    "no_adapter_for_channel",
                    ct2);

                if (noAdapterRows == 0)
                {
                    logger.LogWarning(
                        "ReconcileNotification: lost race settling Sending→FailedPermanent (no_adapter) " +
                        "for notification_id={NotificationId}; another process already settled.",
                        command.NotificationId);
                    outcome = ReconcileOutcome.NotSending;
                    return;
                }

                await EmitAuditAsync(command.TenantId, n, "notification.failed_permanent",
                    new { notification_id = command.NotificationId, failure_category = "no_adapter_for_channel", channel = n.Channel.ToString() },
                    ct2);

                outcome = ReconcileOutcome.Failed;
                return;
            }

            // 3b. Guard: if no provider idempotency key is stored, the capability ladder cannot
            //     proceed (we have no stable key to look up or resend with). Settle FailedPermanent.
            if (string.IsNullOrEmpty(n.ProviderIdempotencyKey))
            {
                var noKeyRows = await repo.MarkFailedAsync(
                    command.TenantId, command.NotificationId,
                    n.Version, NotificationState.Sending,
                    NotificationState.FailedPermanent,
                    "no_provider_idempotency_key",
                    ct2);

                if (noKeyRows == 0)
                {
                    logger.LogWarning(
                        "ReconcileNotification: lost race settling Sending→FailedPermanent (no_key) " +
                        "for notification_id={NotificationId}.",
                        command.NotificationId);
                    outcome = ReconcileOutcome.NotSending;
                    return;
                }

                logger.LogWarning(
                    "ReconcileNotification: notification_id={NotificationId} has no provider_idempotency_key; " +
                    "settled FailedPermanent.",
                    command.NotificationId);

                outcome = ReconcileOutcome.Failed;
                return;
            }

            var now = DateTimeOffset.UtcNow;

            // 4. Capability ladder (§13.6).

            // (a) Lookup-by-key: if provider supports lookup, check whether the message was
            //     already accepted. If found+Accepted → settle without resending (effectively-once).
            if (adapter.SupportsLookup)
            {
                var found = await adapter.LookupByKeyAsync(n.ProviderIdempotencyKey!, ct2);
                if (found is not null && found.Outcome == NotificationSendOutcome.Accepted)
                {
                    var lookupRows = await repo.MarkAcceptedAsync(
                        command.TenantId, command.NotificationId,
                        n.Version, NotificationState.Sending,
                        providerRef: found.ProviderMessageId,
                        ct2);

                    if (lookupRows == 0)
                    {
                        logger.LogWarning(
                            "ReconcileNotification: lost race settling Sending→Accepted (lookup) " +
                            "for notification_id={NotificationId}.",
                            command.NotificationId);
                        outcome = ReconcileOutcome.NotSending;
                        return;
                    }

                    await EmitAuditAsync(command.TenantId, n, "notification.reconciled_lookup",
                        new { notification_id = command.NotificationId, channel = n.Channel.ToString(), provider_ref = found.ProviderMessageId },
                        ct2);

                    // No SendAsync called — effectively-once via lookup.
                    outcome = ReconcileOutcome.AcceptedViaLookup;
                    return;
                }

                // Lookup returned null/not-found: fall through to resend.
                // The resend is effectively the first real send in this case (no dup risk).
            }

            // (b/c) Resend under the SAME stable provider_idempotency_key.
            // Body content is immaterial to provider dedup — what matters is the stable key.
            // Resolve the template to render a real body (F10: reconcile resend uses non-empty body).
            // NOTE: reconcile resends always use the original provider_idempotency_key so that
            //       providers with send-idempotency can deduplicate the re-attempt correctly.
            var reconcileTemplate = await templateResolver.ResolveAsync(n.EventType, n.Channel, "en", ct2);
            var rendered = new RenderedMessage(
                TemplateVersion: n.TemplateVersion,
                Channel: n.Channel,
                Locale: "en",
                Body: reconcileTemplate.BodyTemplate,
                MinimizedRecipient: n.RecipientRef);

            var result = await adapter.SendAsync(n.ProviderIdempotencyKey!, rendered, ct2);

            switch (result.Outcome)
            {
                case NotificationSendOutcome.Accepted:
                    {
                        var acceptedRows = await repo.MarkAcceptedAsync(
                            command.TenantId, command.NotificationId,
                            n.Version, NotificationState.Sending,
                            providerRef: result.ProviderMessageId,
                            ct2);

                        if (acceptedRows == 0)
                        {
                            logger.LogWarning(
                                "ReconcileNotification: lost race settling Sending→Accepted (resend) " +
                                "for notification_id={NotificationId}.",
                                command.NotificationId);
                            outcome = ReconcileOutcome.NotSending;
                            return;
                        }

                        // FM#5: provider supports NEITHER lookup NOR send-idempotency — possible duplicate message.
                        if (!adapter.SupportsLookup && !adapter.SupportsSendIdempotency)
                        {
                            await EmitAuditAsync(command.TenantId, n, "notification.duplicate_risk",
                                new { notification_id = command.NotificationId, channel = n.Channel.ToString() },
                                ct2);

                            logger.LogWarning(
                                "ReconcileNotification: provider for channel {Channel} supports neither " +
                                "send-idempotency nor lookup (FM#5); duplicate message is a documented " +
                                "residual risk for notification_id={NotificationId}.",
                                n.Channel, command.NotificationId);

                            outcome = ReconcileOutcome.AcceptedViaResendDuplicateRisk;
                        }
                        else
                        {
                            await EmitAuditAsync(command.TenantId, n, "notification.reconciled_resend",
                                new { notification_id = command.NotificationId, channel = n.Channel.ToString(), provider_ref = result.ProviderMessageId },
                                ct2);

                            outcome = ReconcileOutcome.AcceptedViaResend;
                        }

                        break;
                    }

                case NotificationSendOutcome.FailedTransient:
                    {
                        // T5a: schedule durable retry (FM#6) or exhaust to FailedPermanent (FM#7).
                        // n.Version is the Sending row's current version.
                        await retryScheduler.HandleTransientAsync(
                            n,
                            expectedVersion: n.Version,
                            result.FailureCategory,
                            ct2);

                        outcome = ReconcileOutcome.Failed;
                        break;
                    }

                case NotificationSendOutcome.FailedPermanent:
                    {
                        var permanentRows = await repo.MarkFailedAsync(
                            command.TenantId, command.NotificationId,
                            n.Version, NotificationState.Sending,
                            NotificationState.FailedPermanent,
                            result.FailureCategory ?? "permanent",
                            ct2);

                        if (permanentRows == 0)
                            logger.LogWarning(
                                "ReconcileNotification: lost race settling Sending→FailedPermanent " +
                                "for notification_id={NotificationId}.",
                                command.NotificationId);

                        outcome = ReconcileOutcome.Failed;
                        break;
                    }

                case NotificationSendOutcome.InvalidRecipient:
                    {
                        var invalidRows = await repo.MarkFailedAsync(
                            command.TenantId, command.NotificationId,
                            n.Version, NotificationState.Sending,
                            NotificationState.InvalidRecipient,
                            "provider_invalid",
                            ct2);

                        if (invalidRows == 0)
                            logger.LogWarning(
                                "ReconcileNotification: lost race settling Sending→InvalidRecipient " +
                                "for notification_id={NotificationId}.",
                                command.NotificationId);

                        outcome = ReconcileOutcome.Failed;
                        break;
                    }
            }
        }, ct);

        return outcome;
    }

    private Task EmitAuditAsync(
        string tenantId,
        Notification n,
        string eventType,
        object metadata,
        CancellationToken ct)
        => auditRepo.AddAsync(new AuditEvent
        {
            TenantId = tenantId,
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
