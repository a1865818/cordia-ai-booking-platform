using Cordia.Application.Handlers.Verification;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;

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

        return DateTimeOffset.UtcNow >= challenge.ExpiresAt
            ? new CheckVerificationResult(CheckVerificationStatusEnum.Expired, null)
            : new CheckVerificationResult(CheckVerificationStatusEnum.Pending, null);
    }

    internal static VerificationGrantView ToGrantView(VerificationChallenge challenge)
        => new()
        {
            GrantId = challenge.GrantId,
            TenantId = challenge.TenantId,
            CustomerId = challenge.CustomerId,
            TargetBookingId = challenge.TargetBookingId,
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
    IUnitOfWork uow)
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

        if (!string.Equals(
                challenge.CodeHash,
                VerificationCode.Hash(challenge.VerificationId, request.Code),
                StringComparison.Ordinal))
        {
            return new CheckVerificationResult(CheckVerificationStatusEnum.Failed, null);
        }

        var grantId = $"grant_{Guid.NewGuid():N}";
        var grantExpiresAt = now + GrantTtl;
        await uow.ExecuteInTransactionAsync(async tx =>
        {
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

            await challenges.MarkVerifiedAsync(request.TenantId, request.VerificationId, grantId, now, tx);

            await auditEvents.AddAsync(new AuditEvent
            {
                TenantId = request.TenantId,
                EventId = $"aud_{Guid.NewGuid():N}",
                EventType = "grant_issued",
                Actor = "verification_engine",
                SubjectRef = challenge.CustomerId ?? string.Empty,
                CorrelationId = request.CorrelationId,
                OperationId = null,
                Metadata = $$"""{"verification_id":"{{challenge.VerificationId}}","grant_id":"{{grantId}}","action":"{{challenge.Action.ToString()}}"}""",
                OccurredAt = now,
            }, tx);
        }, ct);

        var updated = challenge;
        updated.GrantId = grantId;
        updated.VerifiedAt = now;
        updated.ExpiresAt = grantExpiresAt;
        return new CheckVerificationResult(CheckVerificationStatusEnum.Granted, CheckVerificationQueryHandler.ToGrantView(updated));
    }
}
