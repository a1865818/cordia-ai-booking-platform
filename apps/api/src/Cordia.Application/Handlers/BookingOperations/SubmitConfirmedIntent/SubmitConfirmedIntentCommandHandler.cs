using System.Text.Json;
using System.Text.Json.Nodes;
using Cordia.Application.Common;
using Cordia.Application.Handlers.Preorder;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;

/// <summary>
/// Orchestrates the durable acceptance transaction (spec 04 §10.3, Inv-4).
///
/// Disposition logic (§5.1, §13.4):
///   1. Compute canonical hash H over the revision canonical payload.
///   2. Confirm that confirmation.intent_payload_hash == H.Value (§8.8, Inv-1); reject 422 otherwise.
///   3. Pre-check the idempotency store:
///      - Existing row, same hash  → replay 202 from snapshot.
///      - Existing row, diff hash  → 409 Conflict (Inv-3).
///      - No row                   → attempt creator election.
///   4. Inside one UoW transaction: INSERT idempotency_records via ON CONFLICT DO NOTHING.
///      - Won election (1 row): write intent/revision/confirmation/operation/outbox/audit; return 202.
///      - Lost election (0 rows): reload winner and apply §5.1 disposition; return 202 or 409.
/// </summary>
public sealed class SubmitConfirmedIntentCommandHandler(
    ICanonicalRequestHasher hasher,
    IUnitOfWork uow,
    IIdempotencyRepository idempotencyRepo,
    IBookingIntentRepository intentRepo,
    IBookingOperationRepository operationRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo)
    : IRequestHandler<SubmitConfirmedIntentCommand, AcceptanceResult>
{
    // §13.3: retain idempotency records for booking life + dispute/audit window.
    // Concrete value pending tenant/jurisdiction policy; one year is the conservative floor.
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(365);

    public async Task<AcceptanceResult> Handle(
        SubmitConfirmedIntentCommand command,
        CancellationToken ct)
    {
        var request = command.Request;
        var revision = request.Revision;
        var confirmation = request.Confirmation;

        // --- Step 1: Compute canonical hash H over the canonical payload. ---
        // Must use Converter.Settings so union types (DurationMinutes, StartsAt) serialise back to
        // their wire form (number / string / TriState string) rather than the struct layout that
        // default STJ would produce.  This keeps the hash consistent with what the Python agent
        // computed over the original JSON: JsonNode.Parse(rawJson) → CanonicalRequestHasher.Hash.
        var payloadNode = JsonSerializer.SerializeToNode(revision.CanonicalPayload, Cordia.Domain.Generated.Converter.Settings);
        var H = hasher.Hash(payloadNode);

        // --- Step 2: Confirmation hash check (§8.8, Inv-1). ---
        if (confirmation.IntentPayloadHash != H.Value)
            throw new ConfirmationHashMismatchException(
                $"confirmation.intent_payload_hash '{confirmation.IntentPayloadHash}' does not match the computed canonical hash '{H.Value}'.");

        // requestHashStored = version-prefixed (Inv-3 / §5 — preserves canon version without a schema change).
        var requestHashStored = $"{H.Version}:{H.Value}";

        // Map BookingRequest.Operation (OperationTypeEnum from generated namespace) to
        // the Domain.Generated OperationTypeEnum — they are the same type (same assembly);
        // no mapping needed.
        var operationType = revision.CanonicalPayload.Operation;

        // --- Step 3: Pre-check the idempotency store (fast path, outside transaction). ---
        var existing = await idempotencyRepo.GetByKeyAsync(command.TenantId, request.IdempotencyKey, ct);
        if (existing != null)
        {
            return existing.RequestHash == requestHashStored
                ? BuildReplay(existing)
                : new Conflict(new IdempotencyConflict
                {
                    IdempotencyKey = request.IdempotencyKey,
                    ExistingOperationId = existing.OperationId,
                    Message = "Idempotency key already bound to a different request.",
                });
        }

        // --- Step 4: Build the acceptance snapshot (before the transaction). ---
        var now = DateTimeOffset.UtcNow;
        var snapshot = new OperationAcceptedResponse
        {
            OperationId = request.OperationId,
            IntentId = revision.IntentId,
            IntentRevision = revision.Revision,
            Status = "accepted",
            StatusUrl = $"/api/booking-operations/{request.OperationId}",
            AcceptedAt = now,
        };
        var snapshotJson = JsonSerializer.Serialize(snapshot);

        var record = new IdempotencyRecord
        {
            TenantId = command.TenantId,
            IdempotencyKey = request.IdempotencyKey,
            OperationType = operationType,
            RequestHash = requestHashStored,
            OperationId = request.OperationId,
            ProcessingStatus = IdempotencyStatus.Completed,
            ResponseSnapshot = snapshotJson,
            CreatedAt = now,
            LastSeenAt = now,
            RetentionDeadline = now + RetentionWindow,
        };

        // --- Step 5: Creator election + atomic writes inside one UoW transaction. ---
        bool won = await TryElectAndWriteAsync(record, command, revision, confirmation, operationType, request, H.Value, now, ct);

        if (!won)
        {
            // This request lost the concurrent creator race — reload the winner's record.
            // If the winner is null it was deleted by retention in the race window, making
            // the key free again. Retry the whole election up to 2 more times before giving
            // up; a bounded loop guards against spinning while remaining safe.
            const int MaxRetries = 2;
            IdempotencyRecord? winner = null;
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                winner = await idempotencyRepo.GetByKeyAsync(command.TenantId, request.IdempotencyKey, ct);
                if (winner != null)
                    break;

                if (attempt == MaxRetries)
                    throw new InvalidOperationException(
                        $"Creator election lost for key '{request.IdempotencyKey}' but the winner record was not found after {MaxRetries + 1} attempts (retention race).");

                // Key is free again — re-run the election inside a new transaction.
                won = await TryElectAndWriteAsync(record, command, revision, confirmation, operationType, request, H.Value, now, ct);

                if (won)
                    return new Accepted(snapshot);
            }

            return winner!.RequestHash == requestHashStored
                ? BuildReplay(winner)
                : new Conflict(new IdempotencyConflict
                {
                    IdempotencyKey = request.IdempotencyKey,
                    ExistingOperationId = winner.OperationId,
                    Message = "Idempotency key already bound to a different request.",
                });
        }

        return new Accepted(snapshot);
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Runs the creator-election INSERT and, if won, the full atomic acceptance write set
    /// inside one UoW transaction. Returns <c>true</c> if THIS call won the election.
    /// </summary>
    private async Task<bool> TryElectAndWriteAsync(
        IdempotencyRecord record,
        SubmitConfirmedIntentCommand command,
        IntentRevision revision,
        Confirmation confirmation,
        OperationTypeEnum operationType,
        SubmitConfirmedIntentRequest request,
        string payloadHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        bool won = false;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            won = await idempotencyRepo.ElectCreatorAsync(record, ct2);
            if (!won)
                return; // concurrent loser — nothing else written in this transaction

            // Idempotent intent insert: only write the BookingIntent row if it is new.
            // A revision can arrive for an existing intent (e.g. same intent, new operation).
            if (!await intentRepo.IntentExistsAsync(command.TenantId, revision.IntentId, ct2))
            {
                await intentRepo.AddIntentAsync(new BookingIntent
                {
                    TenantId = command.TenantId,
                    IntentId = revision.IntentId,
                    OperationType = operationType,
                    SessionId = revision.SourceSessionId,
                    CreatedAt = now,
                }, ct2);
            }

            await intentRepo.AddRevisionAsync(new BookingIntentRevision
            {
                TenantId = command.TenantId,
                IntentId = revision.IntentId,
                Revision = (int)revision.Revision,
                CanonicalPayload = JsonSerializer.Serialize(revision.CanonicalPayload, Cordia.Domain.Generated.Converter.Settings),
                // BARE hash stored on the revision — the DB trigger compares
                // intent_confirmations.intent_payload_hash against this value (spec 12 §6).
                PayloadHash = payloadHash,
                SchemaVersion = revision.SchemaVersion ?? string.Empty,
                TenantPolicyVersion = revision.TenantPolicyVersion ?? string.Empty,
                PriorRevision = revision.PriorRevision.HasValue ? (int?)revision.PriorRevision.Value : null,
                SupersessionReason = revision.SupersessionReason,
                SourceSessionId = revision.SourceSessionId,
                CreatedAt = now,
            }, ct2);

            await intentRepo.AddConfirmationAsync(new IntentConfirmation
            {
                TenantId = command.TenantId,
                ConfirmationId = confirmation.ConfirmationId,
                IntentId = confirmation.IntentId,
                IntentRevision = (int)confirmation.IntentRevision,
                // BARE hash — must equal revision.payload_hash so the DB trigger passes.
                IntentPayloadHash = confirmation.IntentPayloadHash,
                IdentityRef = confirmation.Identity ?? string.Empty,
                ConfirmationChannel = ConfirmationMappings.MapConfirmationChannel(confirmation.ConfirmationChannel),
                SourceTurnId = confirmation.SourceTurnId ?? string.Empty,
                ReadbackVersion = confirmation.ReadBackVersion ?? string.Empty,
                Disclosures = JsonSerializer.Serialize(confirmation.DisclosuresPresented ?? new List<string>()),
                VerificationLevel = ConfirmationMappings.MapVerificationLevel(confirmation.VerificationLevel),
                ConfirmedAt = confirmation.ConfirmedAt,
            }, ct2);

            // Spec 11 §5.3 — a confirmed revision carrying a non-empty PreorderReference is a
            // pre-order: mark the operation and route it to the pre-order saga (which reserves items,
            // then delegates the whole booking — capacity/payment/create — to the 06 engine) instead
            // of straight to 06. This wires the saga's missing producer. Non-pre-order path unchanged.
            var preorder = revision.CanonicalPayload.Preorder;
            bool isPreorder = preorder is { Lines.Count: > 0 };

            await operationRepo.AddAsync(new BookingOperation
            {
                TenantId = command.TenantId,
                OperationId = request.OperationId,
                IntentId = revision.IntentId,
                IntentRevision = (int)revision.Revision,
                OperationType = operationType,
                Status = OperationStatus.Accepted,
                SupersededOperationId = request.ReplacesOperationId,
                IsPreorder = isPreorder,
                Version = 0,
                CreatedAt = now,
                UpdatedAt = now,
            }, ct2);

            // Payloads carry only non-sensitive identity/routing fields (Inv-12 — no PAN/CVV).
            var outbox = isPreorder
                ? new OutboxMessage
                {
                    TenantId = command.TenantId,
                    MessageId = $"obx_{Guid.NewGuid():N}",
                    OperationId = request.OperationId,
                    MessageType = PreorderSagaMessages.Start, // preorder.commands.start.v1
                    CorrelationId = request.OperationId,
                    CausationId = null,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = request.OperationId,
                        tenant_id = command.TenantId,
                        // The immutable reference copied verbatim from the confirmed revision (part of
                        // the canonical hash, Inv-1/Inv-2). Serialised with the generated converter so
                        // the saga round-trips it against the same property names.
                        preorder_reference = JsonSerializer.SerializeToNode(
                            preorder, Cordia.Domain.Generated.Converter.Settings),
                        // Booking-attached: the saga triggers the 06 engine (07 capacity derived from
                        // the operation's revision). Standalone conversion (§21.4) is a separate path.
                        standalone = false,
                    }),
                    OccurredAt = now,
                }
                : new OutboxMessage
                {
                    TenantId = command.TenantId,
                    MessageId = $"obx_{Guid.NewGuid():N}",
                    OperationId = request.OperationId,
                    MessageType = "booking.command.requested.v1",
                    CorrelationId = request.OperationId,
                    CausationId = null,
                    Payload = JsonSerializer.Serialize(new
                    {
                        operation_id = request.OperationId,
                        intent_id = revision.IntentId,
                        intent_revision = revision.Revision,
                        tenant_id = command.TenantId,
                        operation_type = operationType.ToString().ToLowerInvariant(),
                    }),
                    OccurredAt = now,
                };
            await outboxRepo.AddAsync(outbox, ct2);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "operation.accepted",
                Actor = confirmation.Identity ?? string.Empty,
                SubjectRef = request.OperationId,
                CorrelationId = request.OperationId,
                OperationId = request.OperationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    intent_id = revision.IntentId,
                    intent_revision = revision.Revision,
                }),
                OccurredAt = now,
            }, ct2);
        }, ct);
        return won;
    }

    private static AcceptanceResult BuildReplay(IdempotencyRecord record)
    {
        if (record.ResponseSnapshot is not null)
        {
            var body = JsonSerializer.Deserialize<OperationAcceptedResponse>(record.ResponseSnapshot);
            if (body is not null)
                return new Accepted(body);
        }

        // Snapshot absent (e.g. record committed as in_progress before snapshot was written):
        // reconstruct a minimal response from the durable record fields.
        return new Accepted(new OperationAcceptedResponse
        {
            OperationId = record.OperationId,
            Status = "accepted",
            StatusUrl = $"/api/booking-operations/{record.OperationId}",
            AcceptedAt = record.CreatedAt,
        });
    }
}
