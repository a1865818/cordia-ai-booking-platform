using Cordia.Domain.Generated;
using MediatR;

namespace Cordia.Application.Handlers.Verification.RequestVerification;

public sealed record RequestVerificationCommand(
    string TenantId,
    VerificationRequest Request) : IRequest<RequestVerificationResult>;

/// <param name="DevelopmentCode">
/// The plaintext code, ONLY when <c>Verification:ExposeCodeForDevelopment</c> is on and a new
/// challenge was created. Never persisted: an idempotent replay cannot return it again.
/// </param>
public sealed record RequestVerificationResult(
    RequestVerificationStatusEnum Status,
    VerificationSentResult? Verification,
    string? DevelopmentCode = null);
