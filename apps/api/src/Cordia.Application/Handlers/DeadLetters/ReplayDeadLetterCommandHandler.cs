using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Messaging;
using MediatR;

namespace Cordia.Application.Handlers.DeadLetters;

/// <summary>
/// Implements the §6.1 replay ID rules for dead-lettered messages (spec 05 §6.1, §10, §11, T7).
///
/// §6.1 decision table:
///   1. Never admitted  → preserve original message_id, re-publish (PreservedOriginalMessageId).
///   2. Already admitted (Admitted/Rejected) → new control-message-id, re-publish (NewControlMessageId).
///   3. Inbox records success (Completed) → ack without re-publish (AckedWithoutReadmit).
///
/// Publish-before-mark ordering: the DLQ row is marked Replayed and the audit event written ONLY
/// AFTER a successful transport publish — a publish failure leaves the row Quarantined for retry.
/// For AckedWithoutReadmit there is no publish; mark+audit proceed directly.
/// </summary>
public sealed class ReplayDeadLetterCommandHandler(
    IDeadLetterRepository dlqRepo,
    IInboxRepository inboxRepo,
    IMessageTransport transport,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<ReplayDeadLetterCommand, ReplayDeadLetterResult>
{
    public async Task<ReplayDeadLetterResult> Handle(
        ReplayDeadLetterCommand command,
        CancellationToken ct)
    {
        // ── Step 1: Load DLQ row ────────────────────────────────────────────────────
        var dlq = await dlqRepo.GetAsync(command.TenantId, command.MessageId, command.ConsumerName, ct);
        if (dlq is null)
            throw new DeadLetterNotFoundException(
                $"Dead-letter message not found: tenant={command.TenantId} " +
                $"message_id={command.MessageId} consumer={command.ConsumerName}");

        // ── Step 2: New audit-only replay command id ────────────────────────────────
        var replayCommandId = $"replay-{Guid.NewGuid():N}";

        // ── Step 2a: Idempotent replay guard ────────────────────────────────────────
        // Replaying a row that is no longer Quarantined (already Replayed/Discarded) is a no-op:
        // do NOT re-publish. This makes a repeated operator replay idempotent and avoids a second
        // delivery (which, for the new-control-id branch, would mint a fresh id and bypass dedup).
        if (dlq.ReplayState != DlqReplayState.Quarantined)
            return new ReplayDeadLetterResult(ReplayDecision.AckedWithoutReadmit, replayCommandId, null);

        // ── Step 3: Look up the inbox row (authoritative tenant+consumer+message key) ─
        var inbox = await inboxRepo.GetAsync(command.TenantId, command.ConsumerName, command.MessageId, ct);

        // ── Step 4: §6.1 decision ───────────────────────────────────────────────────
        ReplayDecision decision;
        string? dispatchedMessageId;

        if (inbox is null)
        {
            // Row 1: never admitted → preserve original message_id
            decision = ReplayDecision.PreservedOriginalMessageId;
            dispatchedMessageId = command.MessageId;
        }
        else if (inbox.Outcome == InboxOutcome.Completed)
        {
            // Row 3: inbox already records success → ack without re-admit
            decision = ReplayDecision.AckedWithoutReadmit;
            dispatchedMessageId = null;
        }
        else
        {
            // Row 2: the message was admitted (an inbox row exists). Outcome == Admitted OR Rejected
            // both land here — a Rejected row still proves admission, so reusing the original
            // message_id would hit inbox dedup and silently no-op the resume; a NEW control id
            // drives a fresh resume of the durable step (§6.1 "do not reuse message_id").
            //
            // The control id is DETERMINISTIC (not a fresh GUID): publish (Step 6) happens before
            // the mark/audit txn (Step 7), so if publish succeeds but the mark fails, the row stays
            // Quarantined and a retry republishes the SAME control id → the inbox dedups it → the
            // resume is delivered at-most-once across the publish-before-mark window (Inv-2/Inv-15).
            // Within one Quarantined→Replayed lifecycle there is exactly one intended replay
            // (re-replay is blocked by the Step-2a guard once the row is Replayed).
            decision = ReplayDecision.NewControlMessageId;
            dispatchedMessageId = $"ctl-{command.ConsumerName}-{command.MessageId}";
        }

        // ── Step 5: Parse payload once + extract fields ─────────────────────────────
        var now = DateTimeOffset.UtcNow;
        var payload = JobPayload.ParseOrEmpty(dlq.Payload);
        var operationId = payload.GetStringOrNull("operation_id");

        // ── Step 6: Publish (rows 1 and 2 only) ────────────────────────────────────
        if (decision != ReplayDecision.AckedWithoutReadmit)
        {
            var queue = LogicalQueueRegistry.Resolve(dlq.MessageType);

            var envelope = new JobEnvelope(
                MessageId: dispatchedMessageId!,
                MessageType: dlq.MessageType,
                OccurredAt: now,
                TenantId: dlq.TenantId,
                CorrelationId: operationId ?? command.MessageId,
                CausationId: null,
                OperationId: operationId,
                IntentId: payload.GetStringOrNull("intent_id"),
                IntentRevision: payload.GetInt32OrNull("intent_revision"),
                AttemptHint: 0,
                Payload: payload);

            // Publish OUTSIDE the mark/audit transaction so a publish failure leaves the
            // row Quarantined and the caller can retry (spec 05 §11 / T7 ordering).
            await transport.PublishAsync(envelope, queue, ct);
        }

        // ── Step 7: Atomically claim (Quarantined→Replayed) + write audit (one UoW txn) ──
        // The conditional transition is the atomic claim: under concurrent replays only ONE wins
        // (rows-affected == 1) and writes the audit; the losers (rows-affected == 0) skip the audit.
        // The deterministic control id (Step 4) already collapses any redundant publish via inbox
        // dedup, so the net effect is exactly one resume + one audit even under concurrency.
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            var won = await dlqRepo.TryMarkReplayedAsync(
                command.TenantId, command.MessageId, command.ConsumerName, ct2);
            if (won == 0)
                return; // a concurrent replay already claimed this row — do not write a duplicate audit

            var metadata = JsonSerializer.Serialize(new
            {
                replay_command_id = replayCommandId,
                consumer_name = command.ConsumerName,
                decision = decision.ToString(),
                dispatched_message_id = dispatchedMessageId,
            });

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "dead_letter.replayed",
                Actor = "system",
                SubjectRef = command.MessageId,
                CorrelationId = command.MessageId,
                OperationId = operationId,
                Metadata = metadata,
                OccurredAt = now,
            }, ct2);
        }, ct);

        return new ReplayDeadLetterResult(decision, replayCommandId, dispatchedMessageId);
    }
}
