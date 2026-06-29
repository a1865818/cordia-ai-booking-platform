using Cordia.Domain.Generated;
using MediatR;

namespace Cordia.Application.Handlers.Verification.RequestVerification;

public sealed record RequestVerificationCommand(
    string TenantId,
    VerificationRequest Request) : IRequest<RequestVerificationResult>;

public sealed record RequestVerificationResult(
    RequestVerificationStatusEnum Status,
    VerificationSentResult? Verification);
