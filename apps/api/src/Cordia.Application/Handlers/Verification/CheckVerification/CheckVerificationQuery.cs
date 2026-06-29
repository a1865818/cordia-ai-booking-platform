using Cordia.Domain.Generated;
using MediatR;

namespace Cordia.Application.Handlers.Verification.CheckVerification;

public sealed record CheckVerificationQuery(
    string TenantId,
    string VerificationId) : IRequest<CheckVerificationResult>;

public sealed record VerifyCodeCommand(
    string TenantId,
    string VerificationId,
    string Code,
    string CorrelationId) : IRequest<CheckVerificationResult>;

public sealed record CheckVerificationResult(
    CheckVerificationStatusEnum Status,
    VerificationGrantView? Grant);
