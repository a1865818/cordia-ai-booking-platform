using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Notifications;
using Cordia.Infrastructure.Observability;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cordia.Application.Handlers.Notifications.SendNotification;

/// <summary>
/// Handles notification delivery inside the InboxDeduplicationBehavior transaction (spec 10 §4, §6.2).
///
/// <para><b>Runs inside the inbox transaction.</b> Do NOT open a new <c>IUnitOfWork</c>
/// transaction — all writes share the ambient scoped DbContext. Repository calls that
/// invoke <c>SaveChangesAsync</c> participate in the same ambient txn via EF Core's
/// transaction enlistment.</para>
///
/// <para><b>Inv-8:</b> This handler never calls <c>IBookingOperationRepository.TryTransitionStatusAsync</c>
/// or writes to <c>booking_operations</c> — a notification failure must never roll back
/// or mutate a confirmed booking (§15.3).</para>
///
/// <para><b>Version tracking:</b> a new row is inserted at version 0 (state Pending).
/// Each <c>VersionTokenUpdate</c> settles an old version → bumps to old+1.
/// Settle 1: Pending(v0) → Sending(v1). Settle 2: Sending(v1) → terminal(v2).</para>
/// </summary>
public sealed class SendNotificationCommandHandler(
    INotificationRepository repo,
    INotificationRecipientResolver recipientResolver,
    IConsentGate consentGate,
    INotificationTemplateResolver templateResolver,
    IEnumerable<INotificationProviderAdapter> adapters,
    IBookingOperationRepository opRepo,
    IAuditEventRepository auditRepo,
    NotificationRetryScheduler retryScheduler,
    ILogger<SendNotificationCommandHandler> logger,
    Metrics metrics)
    : IRequestHandler<SendNotificationCommand, Unit>
{
    public async Task<Unit> Handle(SendNotificationCommand command, CancellationToken ct)
    {
        var tenantId = command.TenantId;
        var operationId = command.OperationId;

        // 1. Load operation — must exist and belong to this tenant. Events not yet bound to a
        //    persisted operation (RequiresOperation=false, e.g. a waitlist offer whose op_wl_*
        //    row is minted only at accept) proceed without the row at material revision 0.
        var op = await opRepo.GetAsync(operationId, ct);
        if (op is not null && op.TenantId != tenantId)
            op = null;
        if (op is null && command.RequiresOperation)
        {
            // Inv-13: a true cross-tenant hit (op exists, wrong tenant) is an access-attempt alert
            // signal (§25.4); a plain not-found is not.
            if (op is not null && op.TenantId != tenantId)
                metrics.SecurityTenantCrossScopeAttemptTotal.Add(1);

            logger.LogWarning(
                "SendNotification: operation {OperationId} not found or wrong tenant {TenantId}; ack-and-drop.",
                operationId, tenantId);
            return Unit.Value;
        }

        var now = DateTimeOffset.UtcNow;
        var materialRevision = op?.IntentRevision ?? 0;

        // 2. Resolve recipient (may be null → no valid destination).
        var recipient = await recipientResolver.ResolveAsync(tenantId, operationId, ct);

        var channel = recipient?.Channel ?? NotificationChannel.Sms;
        var locale = recipient?.Locale ?? "en";
        var recipientRef = recipient?.RecipientRef ?? string.Empty;

        // 3. Resolve template to get TemplateVersion (part of notification identity).
        var template = await templateResolver.ResolveAsync(command.EventType, channel, locale, ct);

        // 4. Derive deterministic notification identity (Inv-9).
        var notificationId = NotificationIdentity.Derive(
            tenantId, operationId, command.EventType, recipientRef, template.TemplateVersion, materialRevision);

        // 5. Attempt atomic INSERT … ON CONFLICT DO NOTHING (logical dedup).
        var entity = new Notification
        {
            TenantId = tenantId,
            NotificationId = notificationId,
            // No booking_operations row yet (RequiresOperation=false, e.g. a waitlist offer whose
            // op_wl_* row is minted only at accept) → store NULL so the composite FK is skipped;
            // the op_wl_* id still keys the notification identity above. A non-null value here
            // would violate the FK and the offer notification would die on PG 23503.
            OperationId = op is null ? null : operationId,
            EventType = command.EventType,
            RecipientRef = recipientRef,
            TemplateVersion = template.TemplateVersion,
            MaterialRevision = materialRevision,
            State = NotificationState.Pending,
            Channel = channel,
            Priority = command.Priority,
            ProviderIdempotencyKey = notificationId, // stable across retries (§13.2)
            AttemptCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            Version = 0,
        };

        var inserted = await repo.TryInsertAsync(entity, ct);

        // 6. Logical dedup (Inv-9): a second trigger for the same logical event → suppressed_duplicate.
        if (!inserted)
        {
            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "notification.suppressed_duplicate",
                Actor = "system",
                SubjectRef = notificationId,
                CorrelationId = command.CorrelationId,
                OperationId = operationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    notification_id = notificationId,
                    event_type = command.EventType.ToString(),
                }),
                OccurredAt = now,
            }, ct);

            metrics.NotificationDuplicateSuppressedTotal.Add(1);
            return Unit.Value; // SendAsync is NOT called (Inv-9)
        }

        metrics.NotificationPendingStatusTotal.Add(1);

        // 7. Guard: no valid destination.
        //
        // Checks the DESTINATION, not just the recipient.
        //
        // The resolver always returns a handle so the notification row has a stable ref, which
        // meant `recipient is null` never fired and a booking with nowhere to send still settled
        // as delivered — the platform telling an operator a guest was notified when there was no
        // address at all (Inv-16).
        //
        // This was tried once before and reverted, because the resolver only knew the booking
        // lane: waitlist offer/expiry notifications carry an `op_wl_*` id with no
        // booking_operations row, so every one of them looked destination-less and the guard
        // terminal-failed a working lane. The resolver now reads the waitlist enrollment too, so
        // a null destination finally means what it says.
        if (recipient is null || recipient.Destination is null)
        {
            var noDestRows = await repo.MarkFailedAsync(
                tenantId, notificationId, expectedVersion: 0,
                expectedState: NotificationState.Pending,
                next: NotificationState.InvalidRecipient,
                failureCategory: "no_destination", ct);

            if (noDestRows == 0)
                logger.LogWarning("SendNotification: lost race settling Pending→InvalidRecipient (no_destination) for {NotificationId}.", notificationId);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = tenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "notification.invalid_recipient",
                Actor = "system",
                SubjectRef = notificationId,
                CorrelationId = command.CorrelationId,
                OperationId = operationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    notification_id = notificationId,
                    failure_category = "no_destination",
                }),
                OccurredAt = now,
            }, ct);

            metrics.NotificationInvalidRecipientTotal.Add(1);
            return Unit.Value; // SendAsync is NOT called
        }

        // 8. Consent gate.
        var consent = await consentGate.CheckAsync(tenantId, recipientRef, channel, ct);
        switch (consent)
        {
            case ConsentDecision.Withdrawn:
                var withdrawnRows = await repo.MarkFailedAsync(
                    tenantId, notificationId, expectedVersion: 0,
                    expectedState: NotificationState.Pending,
                    next: NotificationState.InvalidRecipient,
                    failureCategory: "consent_withdrawn", ct);

                if (withdrawnRows == 0)
                    logger.LogWarning("SendNotification: lost race settling Pending→InvalidRecipient (consent_withdrawn) for {NotificationId}.", notificationId);

                await auditRepo.AddAsync(new AuditEvent
                {
                    TenantId = tenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "notification.invalid_recipient",
                    Actor = "system",
                    SubjectRef = notificationId,
                    CorrelationId = command.CorrelationId,
                    OperationId = operationId,
                    Metadata = JsonSerializer.Serialize(new
                    {
                        notification_id = notificationId,
                        failure_category = "consent_withdrawn",
                    }),
                    OccurredAt = now,
                }, ct);

                metrics.NotificationInvalidRecipientTotal.Add(1);
                return Unit.Value; // SendAsync is NOT called

            case ConsentDecision.Suppressed:
                var suppressedRows = await repo.MarkFailedAsync(
                    tenantId, notificationId, expectedVersion: 0,
                    expectedState: NotificationState.Pending,
                    next: NotificationState.SuppressedDuplicate,
                    failureCategory: "policy_suppressed", ct);

                if (suppressedRows == 0)
                    logger.LogWarning("SendNotification: lost race settling Pending→SuppressedDuplicate (policy_suppressed) for {NotificationId}.", notificationId);

                await auditRepo.AddAsync(new AuditEvent
                {
                    TenantId = tenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "notification.suppressed_policy",
                    Actor = "system",
                    SubjectRef = notificationId,
                    CorrelationId = command.CorrelationId,
                    OperationId = operationId,
                    Metadata = JsonSerializer.Serialize(new
                    {
                        notification_id = notificationId,
                        failure_category = "policy_suppressed",
                    }),
                    OccurredAt = now,
                }, ct);
                metrics.NotificationConsentSuppressedTotal.Add(1);

                return Unit.Value; // SendAsync is NOT called

                // ConsentDecision.Allowed → continue below.
        }

        // 9. Render the message body (Inv-12: no PAN/CVV; body carries only safe data).
        var body = template.BodyTemplate; // stubs use the raw template as the body
        var rendered = new RenderedMessage(
            template.TemplateVersion,
            channel,
            locale,
            Body: body,
            MinimizedRecipient: recipientRef,
            // The real address stays off the notification row and out of the logs; it is carried
            // only as far as the adapter. Retry/reconcile re-resolve it rather than persisting it.
            Destination: recipient?.Destination);

        // 10. Settle Pending(v0) → Sending(v1).
        // Row was inserted at version=0, state=Pending. After this settle version becomes 1.
        var sendingRows = await repo.TryTransitionStateAsync(
            tenantId, notificationId,
            expectedVersion: 0,
            expectedState: NotificationState.Pending,
            next: NotificationState.Sending,
            ct);

        if (sendingRows == 0)
        {
            // Lost race — extremely unlikely in single-threaded inbox, but safe to drop.
            logger.LogWarning(
                "SendNotification: lost race settling Pending→Sending for {NotificationId}.", notificationId);
            return Unit.Value;
        }

        // Version is now 1 (Sending).

        // 11. Select adapter for the channel. If none is registered, do NOT throw — the row is
        // already in Sending, so throwing would roll back the inbox admission and cause endless
        // redelivery (a poison loop). Settle to a clean terminal FailedPermanent instead.
        var adapter = adapters.FirstOrDefault(a => a.Channel == channel);
        if (adapter is null)
        {
            var noAdapterRows = await repo.MarkFailedAsync(
                tenantId, notificationId,
                expectedVersion: 1,
                expectedState: NotificationState.Sending,
                next: NotificationState.FailedPermanent,
                failureCategory: "no_adapter_for_channel",
                ct);

            if (noAdapterRows == 0)
                logger.LogWarning("SendNotification: lost race settling Sending→FailedPermanent (no adapter) for {NotificationId}.", notificationId);

            logger.LogWarning(
                "SendNotification: no provider adapter registered for channel {Channel}; settled FailedPermanent for {NotificationId}.",
                channel, notificationId);

            if (noAdapterRows > 0)
            {
                await auditRepo.AddAsync(new AuditEvent
                {
                    TenantId = tenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "notification.failed_permanent",
                    Actor = "system",
                    SubjectRef = notificationId,
                    CorrelationId = command.CorrelationId,
                    OperationId = operationId,
                    Metadata = JsonSerializer.Serialize(new
                    {
                        notification_id = notificationId,
                        failure_category = "no_adapter_for_channel",
                        channel = channel.ToString(),
                    }),
                    OccurredAt = now,
                }, ct);
            }

            return Unit.Value; // SendAsync is NOT called
        }

        NotificationSendResult result;
        try
        {
            result = await adapter.SendAsync(entity.ProviderIdempotencyKey!, rendered, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // F9: guard against adapter exceptions — treat as transient failure so the inbox
            // transaction COMMITS (row settles to FailedTransient + durable retry scheduled)
            // rather than rolling back and causing a poison redelivery loop.
            logger.LogWarning(ex,
                "SendNotification: adapter.SendAsync threw for {NotificationId}; treating as transient.",
                notificationId);
            await retryScheduler.HandleTransientAsync(entity, expectedVersion: 1, "send_exception", ct);
            return Unit.Value;
        }

        // 12. Settle Sending(v1) → terminal state based on provider outcome.
        switch (result.Outcome)
        {
            case NotificationSendOutcome.Accepted:
                var acceptedRows = await repo.MarkAcceptedAsync(
                    tenantId, notificationId,
                    expectedVersion: 1,
                    expectedState: NotificationState.Sending,
                    providerRef: result.ProviderMessageId,
                    ct);

                if (acceptedRows == 0)
                    logger.LogWarning("SendNotification: lost race settling Sending→Accepted for {NotificationId}.", notificationId);

                if (acceptedRows > 0)
                {
                    await auditRepo.AddAsync(new AuditEvent
                    {
                        TenantId = tenantId,
                        EventId = $"evt_{Guid.NewGuid():N}",
                        EventType = "notification.accepted",
                        Actor = "system",
                        SubjectRef = notificationId,
                        CorrelationId = command.CorrelationId,
                        OperationId = operationId,
                        Metadata = JsonSerializer.Serialize(new
                        {
                            notification_id = notificationId,
                            channel = channel.ToString(),
                            provider_ref = result.ProviderMessageId,
                        }),
                        OccurredAt = now,
                    }, ct);
                    metrics.NotificationProviderAcceptedTotal.Add(1);
                }
                break;

            case NotificationSendOutcome.FailedTransient:
                // T5a: schedule durable retry or exhaust to FailedPermanent (FM#6/FM#7).
                // The row is currently at version 1 (Sending). retryScheduler handles the
                // MarkTransientRetryAsync / MarkFailedAsync and job enqueue.
                await retryScheduler.HandleTransientAsync(
                    entity,
                    expectedVersion: 1,
                    result.FailureCategory ?? "transient",
                    ct);
                break;

            case NotificationSendOutcome.FailedPermanent:
                var permanentRows = await repo.MarkFailedAsync(
                    tenantId, notificationId,
                    expectedVersion: 1,
                    expectedState: NotificationState.Sending,
                    next: NotificationState.FailedPermanent,
                    failureCategory: result.FailureCategory ?? "permanent",
                    ct);

                if (permanentRows == 0)
                    logger.LogWarning("SendNotification: lost race settling Sending→FailedPermanent for {NotificationId}.", notificationId);

                if (permanentRows > 0)
                {
                    await auditRepo.AddAsync(new AuditEvent
                    {
                        TenantId = tenantId,
                        EventId = $"evt_{Guid.NewGuid():N}",
                        EventType = "notification.failed_permanent",
                        Actor = "system",
                        SubjectRef = notificationId,
                        CorrelationId = command.CorrelationId,
                        OperationId = operationId,
                        Metadata = JsonSerializer.Serialize(new
                        {
                            notification_id = notificationId,
                            failure_category = result.FailureCategory ?? "permanent",
                        }),
                        OccurredAt = now,
                    }, ct);
                }
                break;

            case NotificationSendOutcome.InvalidRecipient:
                var invalidProvRows = await repo.MarkFailedAsync(
                    tenantId, notificationId,
                    expectedVersion: 1,
                    expectedState: NotificationState.Sending,
                    next: NotificationState.InvalidRecipient,
                    failureCategory: "provider_invalid",
                    ct);

                if (invalidProvRows == 0)
                    logger.LogWarning("SendNotification: lost race settling Sending→InvalidRecipient for {NotificationId}.", notificationId);

                if (invalidProvRows > 0)
                {
                    await auditRepo.AddAsync(new AuditEvent
                    {
                        TenantId = tenantId,
                        EventId = $"evt_{Guid.NewGuid():N}",
                        EventType = "notification.invalid_recipient",
                        Actor = "system",
                        SubjectRef = notificationId,
                        CorrelationId = command.CorrelationId,
                        OperationId = operationId,
                        Metadata = JsonSerializer.Serialize(new
                        {
                            notification_id = notificationId,
                            failure_category = "provider_invalid",
                        }),
                        OccurredAt = now,
                    }, ct);
                    metrics.NotificationInvalidRecipientTotal.Add(1);
                }
                break;
        }

        return Unit.Value;
    }
}
