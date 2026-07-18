using System.Text.Json;
using Cordia.Application.Common;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Observability;
using MediatR;

namespace Cordia.Application.Handlers.ManageBooking.SubmitBookingModification;

public sealed class SubmitBookingModificationCommandHandler(
    ICanonicalRequestHasher hasher,
    IUnitOfWork uow,
    IIdempotencyRepository idempotencyRepo,
    IBookingRepository bookingRepo,
    IBookingIntentRepository intentRepo,
    IBookingOperationRepository operationRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IGrantAuthorizationService grants,
    Metrics metrics)
    : IRequestHandler<SubmitBookingModificationCommand, SubmitBookingModificationResult>
{
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(365);

    public async Task<SubmitBookingModificationResult> Handle(
        SubmitBookingModificationCommand command,
        CancellationToken ct)
    {
        var request = command.Request;
        var bookingId = string.IsNullOrWhiteSpace(request.TargetBookingId)
            ? command.RouteBookingId
            : request.TargetBookingId;

        if (!string.Equals(command.RouteBookingId, bookingId, StringComparison.Ordinal))
            throw new OperationConflictException(command.RouteBookingId, "Route booking id does not match request target_booking_id.");

        var booking = await bookingRepo.GetByIdAsync(command.TenantId, bookingId, ct)
            ?? throw new OperationConflictException(bookingId, "Booking was not found.");

        var intentId = string.IsNullOrWhiteSpace(request.Confirmation?.IntentId)
            ? $"intent_{request.OperationId}"
            : request.Confirmation.IntentId;

        if (request.Confirmation is null ||
            request.Confirmation.IntentRevision != request.IntentRevision ||
            !string.Equals(request.Confirmation.IntentPayloadHash, request.IntentPayloadHash, StringComparison.Ordinal))
        {
            return BuildResult(
                SubmitBookingModificationOutcome.ConfirmationInvalid,
                SubmitBookingModificationStatusEnum.ConfirmationInvalid,
                request,
                intentId,
                DateTimeOffset.UtcNow);
        }

        var requestHash = hasher.Hash(JsonSerializer.SerializeToNode(request, Converter.Settings));
        var requestHashStored = $"{requestHash.Version}:{requestHash.Value}";

        var existing = await idempotencyRepo.GetByKeyAsync(command.TenantId, request.ModificationIdempotencyKey, ct);
        if (existing is not null)
        {
            if (existing.RequestHash == requestHashStored)
                return BuildReplay(existing, SubmitBookingModificationStatusEnum.DuplicateAccepted);

            // Inv-3: key reused with a different payload — alert signal (§25.4).
            metrics.AcceptanceIdempotencyConflictTotal.Add(1);
            return BuildResult(
                SubmitBookingModificationOutcome.Conflict,
                SubmitBookingModificationStatusEnum.IdempotencyConflict,
                request,
                intentId,
                existing.CreatedAt);
        }

        var now = DateTimeOffset.UtcNow;
        var response = BuildResponse(SubmitBookingModificationStatusEnum.Accepted, request, intentId, now);
        var record = new IdempotencyRecord
        {
            TenantId = command.TenantId,
            IdempotencyKey = request.ModificationIdempotencyKey,
            OperationType = OperationTypeEnum.Modify,
            RequestHash = requestHashStored,
            OperationId = request.OperationId,
            ProcessingStatus = IdempotencyStatus.Completed,
            ResponseSnapshot = JsonSerializer.Serialize(response, Converter.Settings),
            CreatedAt = now,
            LastSeenAt = now,
            RetentionDeadline = now + RetentionWindow,
        };

        bool won;
        try
        {
            won = await TryElectAndWriteAsync(record, command, booking, intentId, now, ct);
        }
        catch (StaleBookingVersionException)
        {
            return BuildResult(
                SubmitBookingModificationOutcome.StaleVersion,
                SubmitBookingModificationStatusEnum.StaleVersion,
                request,
                intentId,
                DateTimeOffset.UtcNow);
        }
        if (!won)
        {
            var winner = await idempotencyRepo.GetByKeyAsync(command.TenantId, request.ModificationIdempotencyKey, ct)
                ?? throw new InvalidOperationException($"Creator election lost for key '{request.ModificationIdempotencyKey}' but winner was not found.");

            if (winner.RequestHash == requestHashStored)
                return BuildReplay(winner, SubmitBookingModificationStatusEnum.DuplicateAccepted);

            // Inv-3: key reused with a different payload (post-election race) — alert signal (§25.4).
            metrics.AcceptanceIdempotencyConflictTotal.Add(1);
            return BuildResult(
                SubmitBookingModificationOutcome.Conflict,
                SubmitBookingModificationStatusEnum.IdempotencyConflict,
                request,
                intentId,
                winner.CreatedAt);
        }

        return new SubmitBookingModificationResult(SubmitBookingModificationOutcome.Accepted, response);
    }

    private async Task<bool> TryElectAndWriteAsync(
        IdempotencyRecord record,
        SubmitBookingModificationCommand command,
        Booking booking,
        string intentId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var request = command.Request;
        bool won = false;
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            won = await idempotencyRepo.ElectCreatorAsync(record, ct2);
            if (!won)
                return;

            var transitioned = await bookingRepo.TryTransitionLifecycleAsync(
                command.TenantId,
                booking.BookingId,
                request.ExpectedBookingVersion,
                BookingLifecycleStatus.ModificationPending,
                now,
                ct2);
            if (transitioned == 0)
            {
                var currentVersion = await bookingRepo.GetCurrentVersionAsync(command.TenantId, booking.BookingId, ct2)
                    ?? booking.Version;
                throw new StaleBookingVersionException(booking.BookingId, request.ExpectedBookingVersion, currentVersion);
            }

            await grants.ConsumeGrantAsync(
                command.TenantId,
                request.GrantId,
                booking.CustomerId,
                VerificationActionEnum.Modify,
                booking.BookingId,
                targetType: "booking",
                ct: ct2);

            if (!await intentRepo.IntentExistsAsync(command.TenantId, intentId, ct2))
            {
                await intentRepo.AddIntentAsync(new BookingIntent
                {
                    TenantId = command.TenantId,
                    IntentId = intentId,
                    OperationType = OperationTypeEnum.Modify,
                    SessionId = request.Confirmation.SourceTurnId,
                    CreatedAt = now,
                }, ct2);
            }

            var existingRevision = await intentRepo.GetRevisionAsync(command.TenantId, intentId, (int)request.IntentRevision, ct2);
            if (existingRevision is not null)
            {
                if (!string.Equals(existingRevision.PayloadHash, request.IntentPayloadHash, StringComparison.Ordinal))
                    throw new OperationConflictException(intentId, "Intent revision already exists with a different payload hash.");
            }
            else
            {
                await intentRepo.AddRevisionAsync(new BookingIntentRevision
                {
                    TenantId = command.TenantId,
                    IntentId = intentId,
                    Revision = (int)request.IntentRevision,
                    CanonicalPayload = JsonSerializer.Serialize(request, Converter.Settings),
                    PayloadHash = request.IntentPayloadHash,
                    SchemaVersion = "manage-booking.modification.v1",
                    TenantPolicyVersion = "manage-booking.v1",
                    SourceSessionId = request.Confirmation.SourceTurnId,
                    CreatedAt = now,
                }, ct2);
            }

            await intentRepo.AddConfirmationAsync(new IntentConfirmation
            {
                TenantId = command.TenantId,
                ConfirmationId = request.Confirmation.ConfirmationId,
                IntentId = intentId,
                IntentRevision = (int)request.IntentRevision,
                IntentPayloadHash = request.IntentPayloadHash,
                IdentityRef = request.Confirmation.Identity ?? string.Empty,
                ConfirmationChannel = ConfirmationMappings.MapConfirmationChannel(request.Confirmation.ConfirmationChannel),
                SourceTurnId = request.Confirmation.SourceTurnId ?? string.Empty,
                ReadbackVersion = request.Confirmation.ReadBackVersion ?? string.Empty,
                Disclosures = JsonSerializer.Serialize(request.Confirmation.DisclosuresPresented ?? []),
                VerificationLevel = ConfirmationMappings.MapVerificationLevel(request.Confirmation.VerificationLevel),
                ConfirmedAt = request.Confirmation.ConfirmedAt,
            }, ct2);

            await operationRepo.AddAsync(new BookingOperation
            {
                TenantId = command.TenantId,
                OperationId = request.OperationId,
                IntentId = intentId,
                IntentRevision = (int)request.IntentRevision,
                OperationType = OperationTypeEnum.Modify,
                Status = OperationStatus.Accepted,
                BookingId = booking.BookingId,
                Version = 0,
                CreatedAt = now,
                UpdatedAt = now,
            }, ct2);

            await outboxRepo.AddAsync(new OutboxMessage
            {
                TenantId = command.TenantId,
                MessageId = $"obx_{Guid.NewGuid():N}",
                OperationId = request.OperationId,
                MessageType = "booking.command.requested.v1",
                CorrelationId = request.OperationId,
                Payload = JsonSerializer.Serialize(new
                {
                    operation_id = request.OperationId,
                    intent_id = intentId,
                    intent_revision = request.IntentRevision,
                    tenant_id = command.TenantId,
                    booking_id = booking.BookingId,
                    operation_type = "modify",
                }),
                OccurredAt = now,
            }, ct2);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "booking_modification_accepted",
                Actor = request.Confirmation.Identity ?? string.Empty,
                SubjectRef = booking.BookingId,
                CorrelationId = request.OperationId,
                OperationId = request.OperationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    intent_id = intentId,
                    intent_revision = request.IntentRevision,
                    expected_booking_version = request.ExpectedBookingVersion,
                }),
                OccurredAt = now,
            }, ct2);
        }, ct);

        return won;
    }

    private static SubmitBookingModificationResult BuildReplay(
        IdempotencyRecord record,
        SubmitBookingModificationStatusEnum replayStatus)
    {
        SubmitBookingModificationResponse? body = null;
        if (record.ResponseSnapshot is not null)
            body = JsonSerializer.Deserialize<SubmitBookingModificationResponse>(record.ResponseSnapshot, Converter.Settings);

        body ??= new SubmitBookingModificationResponse(
            replayStatus,
            record.OperationId,
            $"intent_{record.OperationId}",
            0,
            $"/api/booking-operations/{record.OperationId}",
            record.CreatedAt);

        body = body with { Status = replayStatus };
        return new SubmitBookingModificationResult(SubmitBookingModificationOutcome.Accepted, body);
    }

    private static SubmitBookingModificationResult BuildResult(
        SubmitBookingModificationOutcome outcome,
        SubmitBookingModificationStatusEnum status,
        SubmitModificationInput request,
        string intentId,
        DateTimeOffset acceptedAt)
        => new(outcome, BuildResponse(status, request, intentId, acceptedAt));

    private static SubmitBookingModificationResponse BuildResponse(
        SubmitBookingModificationStatusEnum status,
        SubmitModificationInput request,
        string intentId,
        DateTimeOffset acceptedAt)
        => new(
            status,
            request.OperationId,
            intentId,
            request.IntentRevision,
            $"/api/booking-operations/{request.OperationId}",
            acceptedAt);
}
