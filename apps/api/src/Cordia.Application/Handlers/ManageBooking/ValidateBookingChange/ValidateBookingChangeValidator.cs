using FluentValidation;

namespace Cordia.Application.Handlers.ManageBooking.ValidateBookingChange;

public sealed class ValidateBookingChangeValidator : AbstractValidator<ValidateBookingChangeQuery>
{
    public ValidateBookingChangeValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.TargetBookingId).NotEmpty();
        RuleFor(x => x.ExpectedBookingVersion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.GrantId).NotEmpty();
    }
}
