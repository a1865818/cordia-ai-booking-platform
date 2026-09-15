using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cordia.Application.Handlers.Verification;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Options;

namespace Cordia.Application.Handlers.Verification.CheckVerification;

public sealed class CheckVerificationQueryHandler(IVerificationChallengeRepository challenges)
    : IRequestHandler<CheckVerificationQuery, CheckVerificationResult>
{
    public async Task<CheckVerificationResult> Handle(CheckVerificationQuery request, CancellationToken ct)
    {
        var challenge = await challenges.GetByIdAsync(request.TenantId, request.VerificationId, ct);
        if (challenge is null)
            return new CheckVerificationResult(CheckVerificationStatusEnum.Failed, null);

        if (challenge.VerifiedAt is not null && challenge.GrantId is not null)
            return new CheckVerificationResult(CheckVerificationStatusEnum.Granted, ToGrantView(challenge));

        // A locked challenge will never grant, so reporting it as pending would keep a poller
        // waiting on something that cannot happen.
        if (challenge.FailedAttempts >= VerificationCode.MaxFailedAttempts)
            return new CheckVerificationResult(CheckVerificationStatusEnum.Failed, null);

        return DateTimeOffset.UtcNow >= challenge.ExpiresAt
            ? new CheckVerificationResult(CheckVerificationStatusEnum.Expired, null)
            : new CheckVerificationResult(CheckVerificationStatusEnum.Pending, null);
    }

    internal static VerificationGrantView ToGrantView(VerificationChallenge challenge)
        => new()
        {
            GrantId = challenge.GrantId ?? string.Empty,
            TenantId = challenge.TenantId,
            CustomerId = challenge.CustomerId ?? string.Empty,
            TargetBookingId = challenge.TargetBookingId ?? string.Empty,
            AllowedAction = challenge.Action,
            ExpiresAt = challenge.ExpiresAt,
            Scope = string.IsNullOrWhiteSpace(challenge.TargetBookingId)
                ? Scope.Session
                : Scope.Operation,
        };
}

public sealed class VerifyCodeCommandHandler(
    IVerificationChallengeRepository challenges,
    IVerificationGrantRepository grants,
    IAuditEventRepository auditEvents,
    IUnitOfWork uow,
    IOptions<VerificationCodeOptions> codeOptions)
    : IRequestHandler<VerifyCodeCommand, CheckVerificationResult>
{
    private static readonly TimeSpan GrantTtl = TimeSpan.FromMinutes(15);

    public async Task<CheckVerificationResult> Handle(VerifyCodeCommand request, CancellationToken ct)
    {
        var challenge = await challenges.GetByIdAsync(request.TenantId, request.VerificationId, ct);
        if (challenge is null)
            return new CheckVerificationResult(CheckVerificationStatusEnum.Failed, null);

        var now = DateTimeOffset.UtcNow;
        if (now >= challenge.ExpiresAt)
            return new CheckVerificationResult(CheckVerificationStatusEnum.Expired, null);

        if (challenge.VerifiedAt is not null && challenge.GrantId is not null)
            return new CheckVerificationResult(CheckVerificationStatusEnum.Granted, CheckVerificationQueryHandler.ToGrantView(challenge));

        // #71: a challenge that has taken MaxFailedAttempts wrong codes is locked — the right code
        // no longer verifies it either, or the limit would only slow a guesser down.
        if (challenge.FailedAttempts >= VerificationCode.MaxFailedAttempts)
            return new CheckVerificationResult(CheckVerificationStatusEnum.Failed, null);

        // Compared as fixed-length hex digests in constant time, so response timing says nothing
        // about how much of a guess was right.
        var submittedHash = VerificationCode.Hash(codeOptions.Value, challenge.VerificationId, request.Code);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(challenge.CodeHash),
                Encoding.ASCII.GetBytes(submittedHash)))
        {
            // Atomic and bounded in SQL, so parallel guesses cannot each read "4 of 5" and all try.
            await challenges.RecordFailedAttemptAsync(
                request.TenantId, request.VerificationId, VerificationCode.MaxFailedAttempts, ct);
            return new CheckVerificationResult(CheckVerificationStatusEnum.Failed, null);
        }

        var grantId = $"grant_{Guid.NewGuid():N}";
        var grantExpiresAt = now + GrantTtl;
        var issued = false;
        await uow.ExecuteInTransactionAsync(async tx =>
        {
            var marked = await challenges.MarkVerifiedAsync(
                request.TenantId,
                request.VerificationId,
                grantId,
                now,
                grantExpiresAt,
                VerificationCode.MaxFailedAttempts,
                tx);
            if (marked == 0)
                return;

            await grants.AddAsync(new VerificationGrant
            {
                TenantId = request.TenantId,
                GrantId = grantId,
                CustomerRef = challenge.CustomerId ?? string.Empty,
                TargetBookingId = challenge.TargetBookingId,
                AllowedAction = challenge.Action,
                VerificationLevel = VerificationLevel.IdentityVerified,
                SessionOrOperationRef = challenge.TargetBookingId ?? challenge.VerificationId,
                IssuedAt = now,
                ExpiresAt = grantExpiresAt,
            }, tx);

            await auditEvents.AddAsync(new AuditEvent
            {
                TenantId = request.TenantId,
                EventId = $"aud_{Guid.NewGuid():N}",
                EventType = "grant_issued",
                Actor = "verification_engine",
                SubjectRef = challenge.CustomerId ?? string.Empty,
                CorrelationId = request.CorrelationId,
                OperationId = null,
                Metadata = JsonSerializer.Serialize(new
                {
                    verification_id = challenge.VerificationId,
                    grant_id = grantId,
                    action = challenge.Action.ToString(),
                }),
                OccurredAt = now,
            }, tx);

            issued = true;
        }, ct);

        if (!issued)
        {
            var winner = await challenges.GetByIdAsync(request.TenantId, request.VerificationId, ct);
            return winner is { VerifiedAt: not null, GrantId: not null }
                ? new CheckVerificationResult(CheckVerificationStatusEnum.Granted, CheckVerificationQueryHandler.ToGrantView(winner))
                : new CheckVerificationResult(CheckVerificationStatusEnum.Failed, null);
        }

        var updated = challenge;
        updated.GrantId = grantId;
        updated.VerifiedAt = now;
        updated.ExpiresAt = grantExpiresAt;
        return new CheckVerificationResult(CheckVerificationStatusEnum.Granted, CheckVerificationQueryHandler.ToGrantView(updated));
    }
}
