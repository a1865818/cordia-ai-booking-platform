using FluentValidation;

namespace Cordia.Application.Handlers.Waitlist.Cancel;

/// <summary>Structural validation for <see cref="CancelWaitlistEnrollmentCommand"/>. Failures → HTTP 400.</summary>
public sealed class CancelWaitlistEnrollmentValidator : AbstractValidator<CancelWaitlistEnrollmentCommand>
{
    public CancelWaitlistEnrollmentValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.EnrollmentId).NotEmpty();
    }
}
