using FluentValidation;

namespace Cordia.Application.Handlers.ManageBooking.ValidateBookingChange;

public sealed class ValidateBookingChangeValidator : AbstractValidator<ValidateBookingChangeQuery>
{
    public ValidateBookingChangeValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.TargetBookingId).NotEmpty();
        RuleFor(x => x.ExpectedBookingVersion).GreaterThanOrEqualTo(0);

        // Deliberately NO rule on GrantId. Spec 08 §12 authorizes this with a grant OR an
        // authenticated staff role, so "no grant" is not a malformed request — it is the shape
        // every console call takes. A NotEmpty rule here ran in `ValidationPipelineBehavior`,
        // ahead of the handler, and answered 400 before the role could ever be considered: it
        // made the alternative door unreachable and reported an authorization question as a
        // schema error. Absence of BOTH is a 403, and the handler is where that is decided.
    }
}
