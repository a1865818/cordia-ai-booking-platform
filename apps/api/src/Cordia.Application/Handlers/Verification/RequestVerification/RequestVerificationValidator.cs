using FluentValidation;

namespace Cordia.Application.Handlers.Verification.RequestVerification;

public sealed class RequestVerificationValidator : AbstractValidator<RequestVerificationCommand>
{
    public RequestVerificationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request).NotNull();
        RuleFor(x => x.Request.VerificationIdempotencyKey).NotEmpty();
        RuleFor(x => x.Request.CustomerId)
            .NotEmpty()
            .WithMessage("customer_id is required for local verification.");
    }
}
