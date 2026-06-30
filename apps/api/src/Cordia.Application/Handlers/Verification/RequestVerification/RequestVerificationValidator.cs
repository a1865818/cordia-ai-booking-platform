using FluentValidation;

namespace Cordia.Application.Handlers.Verification.RequestVerification;

public sealed class RequestVerificationValidator : AbstractValidator<RequestVerificationCommand>
{
    public RequestVerificationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request).NotNull();
        When(x => x.Request is not null, () =>
        {
            RuleFor(x => x.Request.VerificationIdempotencyKey).NotEmpty();
            RuleFor(x => x.Request.CustomerId)
                .NotEmpty()
                .When(x => string.IsNullOrWhiteSpace(x.Request.TargetBookingId))
                .WithMessage("customer_id is required for session-scoped verification.");
        });
    }
}
