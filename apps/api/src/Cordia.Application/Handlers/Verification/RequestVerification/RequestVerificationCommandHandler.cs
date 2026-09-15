using Cordia.Application.Handlers.Verification;
using Cordia.Domain.Entities;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Options;

namespace Cordia.Application.Handlers.Verification.RequestVerification;

public sealed class RequestVerificationCommandHandler(
    IVerificationChallengeRepository challenges,
    IBookingRepository bookings,
    IOptions<VerificationCodeOptions> codeOptions)
    : IRequestHandler<RequestVerificationCommand, RequestVerificationResult>
{
    private static readonly TimeSpan ChallengeTtl = TimeSpan.FromMinutes(10);

    public async Task<RequestVerificationResult> Handle(
        RequestVerificationCommand command,
        CancellationToken ct)
    {
        var request = command.Request;
        var existing = await challenges.GetByIdempotencyKeyAsync(
            command.TenantId,
            request.VerificationIdempotencyKey,
            ct);

        if (existing is not null)
            return new RequestVerificationResult(RequestVerificationStatusEnum.AlreadySent, ToResult(existing));

        var targetBookingId = string.IsNullOrWhiteSpace(request.TargetBookingId) ? null : request.TargetBookingId;
        var customerId = request.CustomerId;
        if (targetBookingId is not null)
        {
            var booking = await bookings.GetByIdAsync(command.TenantId, targetBookingId, ct)
                ?? throw new OperationConflictException(targetBookingId, "Booking was not found.");
            customerId = booking.CustomerId;
        }

        var now = DateTimeOffset.UtcNow;
        var verificationId = $"ver_{Guid.NewGuid():N}";
        var challenge = new VerificationChallenge
        {
            TenantId = command.TenantId,
            VerificationId = verificationId,
            IdempotencyKey = request.VerificationIdempotencyKey,
            CustomerId = customerId,
            TargetBookingId = targetBookingId,
            Action = request.Action,
            Channel = request.Channel,
            CodeHash = VerificationCode.Hash(codeOptions.Value, verificationId, VerificationCode.DevelopmentCode),
            RequestedAt = now,
            ExpiresAt = now + ChallengeTtl,
        };

        if (await challenges.TryAddAsync(challenge, ct))
            return new RequestVerificationResult(RequestVerificationStatusEnum.Sent, ToResult(challenge));

        var winner = await challenges.GetByIdempotencyKeyAsync(command.TenantId, request.VerificationIdempotencyKey, ct)
            ?? throw new InvalidOperationException($"Verification idempotency key '{request.VerificationIdempotencyKey}' conflicted but winner was not found.");
        return new RequestVerificationResult(RequestVerificationStatusEnum.AlreadySent, ToResult(winner));
    }

    private static VerificationSentResult ToResult(VerificationChallenge challenge)
        => new()
        {
            VerificationId = challenge.VerificationId,
            Channel = challenge.Channel,
            ExpiresAt = challenge.ExpiresAt,
            MaskedDestination = challenge.Channel == VerificationRequestChannel.Email
                ? "verified email destination"
                : "verified sms destination",
        };
}
