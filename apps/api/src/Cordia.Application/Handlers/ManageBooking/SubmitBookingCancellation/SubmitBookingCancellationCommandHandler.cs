using System.Text.Json;
using Cordia.Application.Common;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.ManageBooking.SubmitBookingCancellation;

public sealed class SubmitBookingCancellationCommandHandler(
    ICanonicalRequestHasher hasher,
    IUnitOfWork uow,
    IIdempotencyRepository idempotencyRepo,
    IBookingRepository bookingRepo,
    IBookingIntentRepository intentRepo,
    IBookingOperationRepository operationRepo,
    IOutboxRepository outboxRepo,
    IAuditEventRepository auditRepo,
    IGrantAuthorizationService grants)
    : IRequestHandler<SubmitBookingCancellationCommand, SubmitBookingCancellationResult>
{
    private static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(365);

    public async Task<SubmitBookingCancellationResult> Handle(
        SubmitBookingCancellationCommand command,
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

        await grants.RequireGrantAsync(
            command.TenantId,
            request.GrantId,
            booking.CustomerId,
            VerificationActionEnum.Cancel,
            bookingId,
            ct);

        var intentId = string.IsNullOrWhiteSpace(request.Confirmation?.IntentId)
            ? $"intent_{request.OperationId}"
            : request.Confirmation.IntentId;
        var intentRevision = request.Confirmation?.IntentRevision > 0
            ? request.Confirmation.IntentRevision
            : 1;

        if (booking.LifecycleStatus == BookingLifecycleStatus.Cancelled)
        {
            return BuildResult(
                SubmitBookingCancellationOutcome.AlreadyCancelled,
                SubmitBookingCancellationStatusEnum.AlreadyCancelled,
                request,
                intentId,
                intentRevision,
                DateTimeOffset.UtcNow);
        }

        var requestHash = hasher.Hash(JsonSerializer.SerializeToNode(request, Converter.Settings));
        var requestHashStored = $"{requestHash.Version}:{requestHash.Value}";

        var existing = await idempotencyRepo.GetByKeyAsync(command.TenantId, request.CancellationIdempotencyKey, ct);
        if (existing is not null)
        {
            return existing.RequestHash == requestHashStored
                ? BuildReplay(existing, SubmitBookingCancellationStatusEnum.DuplicateAccepted)
                : BuildResult(
                    SubmitBookingCancellationOutcome.Conflict,
                    SubmitBookingCancellationStatusEnum.IdempotencyConflict,
                    request,
                    intentId,
                    intentRevision,
                    existing.CreatedAt);
        }

        var now = DateTimeOffset.UtcNow;
        var response = BuildResponse(SubmitBookingCancellationStatusEnum.Accepted, request, intentId, intentRevision, now);
        var record = new IdempotencyRecord
        {
            TenantId = command.TenantId,
            IdempotencyKey = request.CancellationIdempotencyKey,
            OperationType = OperationTypeEnum.Cancel,
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
            won = await TryElectAndWriteAsync(record, command, booking, intentId, intentRevision, requestHash.Value, now, ct);
        }
        catch (StaleBookingVersionException)
        {
            return BuildResult(
                SubmitBookingCancellationOutcome.StaleVersion,
                SubmitBookingCancellationStatusEnum.StaleVersion,
                request,
                intentId,
                intentRevision,
                DateTimeOffset.UtcNow);
        }

        if (!won)
        {
            var winner = await idempotencyRepo.GetByKeyAsync(command.TenantId, request.CancellationIdempotencyKey, ct)
                ?? throw new InvalidOperationException($"Creator election lost for key '{request.CancellationIdempotencyKey}' but winner was not found.");

            return winner.RequestHash == requestHashStored
                ? BuildReplay(winner, SubmitBookingCancellationStatusEnum.DuplicateAccepted)
                : BuildResult(
                    SubmitBookingCancellationOutcome.Conflict,
                    SubmitBookingCancellationStatusEnum.IdempotencyConflict,
                    request,
                    intentId,
                    intentRevision,
                    winner.CreatedAt);
        }

        return new SubmitBookingCancellationResult(SubmitBookingCancellationOutcome.Accepted, response);
    }

    private async Task<bool> TryElectAndWriteAsync(
        IdempotencyRecord record,
        SubmitBookingCancellationCommand command,
        Booking booking,
        string intentId,
        long intentRevision,
        string requestPayloadHash,
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
                BookingLifecycleStatus.CancellationPending,
                now,
                ct2);
            if (transitioned == 0)
            {
                var currentVersion = await bookingRepo.GetCurrentVersionAsync(command.TenantId, booking.BookingId, ct2)
                    ?? booking.Version;
                throw new StaleBookingVersionException(booking.BookingId, request.ExpectedBookingVersion, currentVersion);
            }

            if (!await intentRepo.IntentExistsAsync(command.TenantId, intentId, ct2))
            {
                await intentRepo.AddIntentAsync(new BookingIntent
                {
                    TenantId = command.TenantId,
                    IntentId = intentId,
                    OperationType = OperationTypeEnum.Cancel,
                    SessionId = request.Confirmation?.SourceTurnId,
                    CreatedAt = now,
                }, ct2);
            }

            var revisionPayloadHash = request.Confirmation?.IntentPayloadHash ?? requestPayloadHash;
            await intentRepo.AddRevisionAsync(new BookingIntentRevision
            {
                TenantId = command.TenantId,
                IntentId = intentId,
                Revision = (int)intentRevision,
                CanonicalPayload = JsonSerializer.Serialize(request, Converter.Settings),
                PayloadHash = revisionPayloadHash,
                SchemaVersion = "manage-booking.cancellation.v1",
                TenantPolicyVersion = "manage-booking.v1",
                SourceSessionId = request.Confirmation?.SourceTurnId,
                CreatedAt = now,
            }, ct2);

            if (request.Confirmation is not null)
            {
                await intentRepo.AddConfirmationAsync(new IntentConfirmation
                {
                    TenantId = command.TenantId,
                    ConfirmationId = request.Confirmation.ConfirmationId,
                    IntentId = intentId,
                    IntentRevision = (int)intentRevision,
                    IntentPayloadHash = revisionPayloadHash,
                    IdentityRef = request.Confirmation.Identity ?? string.Empty,
                    ConfirmationChannel = ConfirmationMappings.MapConfirmationChannel(request.Confirmation.ConfirmationChannel),
                    SourceTurnId = request.Confirmation.SourceTurnId ?? string.Empty,
                    ReadbackVersion = request.Confirmation.ReadBackVersion ?? string.Empty,
                    Disclosures = JsonSerializer.Serialize(request.Confirmation.DisclosuresPresented ?? []),
                    VerificationLevel = ConfirmationMappings.MapVerificationLevel(request.Confirmation.VerificationLevel),
                    ConfirmedAt = request.Confirmation.ConfirmedAt,
                }, ct2);
            }

            await operationRepo.AddAsync(new BookingOperation
            {
                TenantId = command.TenantId,
                OperationId = request.OperationId,
                IntentId = intentId,
                IntentRevision = (int)intentRevision,
                OperationType = OperationTypeEnum.Cancel,
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
                    intent_revision = intentRevision,
                    tenant_id = command.TenantId,
                    booking_id = booking.BookingId,
                    operation_type = "cancel",
                    reason_code = request.ReasonCode,
                }),
                OccurredAt = now,
            }, ct2);

            await auditRepo.AddAsync(new AuditEvent
            {
                TenantId = command.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = "booking_cancellation_accepted",
                Actor = request.Confirmation?.Identity ?? string.Empty,
                SubjectRef = booking.BookingId,
                CorrelationId = request.OperationId,
                OperationId = request.OperationId,
                Metadata = JsonSerializer.Serialize(new
                {
                    intent_id = intentId,
                    intent_revision = intentRevision,
                    expected_booking_version = request.ExpectedBookingVersion,
                    reason_code = request.ReasonCode,
                }),
                OccurredAt = now,
            }, ct2);
        }, ct);

        return won;
    }

    private static SubmitBookingCancellationResult BuildReplay(
        IdempotencyRecord record,
        SubmitBookingCancellationStatusEnum replayStatus)
    {
        SubmitBookingCancellationResponse? body = null;
        if (record.ResponseSnapshot is not null)
            body = JsonSerializer.Deserialize<SubmitBookingCancellationResponse>(record.ResponseSnapshot, Converter.Settings);

        body ??= new SubmitBookingCancellationResponse(
            replayStatus,
            record.OperationId,
            $"intent_{record.OperationId}",
            1,
            $"/api/booking-operations/{record.OperationId}",
            record.CreatedAt);

        body = body with { Status = replayStatus };
        return new SubmitBookingCancellationResult(SubmitBookingCancellationOutcome.Accepted, body);
    }

    private static SubmitBookingCancellationResult BuildResult(
        SubmitBookingCancellationOutcome outcome,
        SubmitBookingCancellationStatusEnum status,
        SubmitCancellationInput request,
        string intentId,
        long intentRevision,
        DateTimeOffset acceptedAt)
        => new(outcome, BuildResponse(status, request, intentId, intentRevision, acceptedAt));

    private static SubmitBookingCancellationResponse BuildResponse(
        SubmitBookingCancellationStatusEnum status,
        SubmitCancellationInput request,
        string intentId,
        long intentRevision,
        DateTimeOffset acceptedAt)
        => new(
            status,
            request.OperationId,
            intentId,
            intentRevision,
            $"/api/booking-operations/{request.OperationId}",
            acceptedAt);
}
