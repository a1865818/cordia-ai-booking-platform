using Cordia.Application.Handlers.Verification;
using Cordia.Domain.Entities;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Verification.RequestVerification;

public sealed class RequestVerificationCommandHandler(IVerificationChallengeRepository challenges)
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

        var now = DateTimeOffset.UtcNow;
        var verificationId = $"ver_{Guid.NewGuid():N}";
        var challenge = new VerificationChallenge
        {
            TenantId = command.TenantId,
            VerificationId = verificationId,
            IdempotencyKey = request.VerificationIdempotencyKey,
            CustomerId = request.CustomerId,
            TargetBookingId = string.IsNullOrWhiteSpace(request.TargetBookingId) ? null : request.TargetBookingId,
            Action = request.Action,
            Channel = request.Channel,
            CodeHash = VerificationCode.Hash(verificationId, VerificationCode.DevelopmentCode),
            RequestedAt = now,
            ExpiresAt = now + ChallengeTtl,
        };

        await challenges.AddAsync(challenge, ct);
        return new RequestVerificationResult(RequestVerificationStatusEnum.Sent, ToResult(challenge));
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
