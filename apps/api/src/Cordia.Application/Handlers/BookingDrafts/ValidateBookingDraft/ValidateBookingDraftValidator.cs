using FluentValidation;

namespace Cordia.Application.Handlers.BookingDrafts.ValidateBookingDraft;

/// <summary>
/// Structural validator for <see cref="ValidateBookingDraftQuery"/>.
/// <para>
/// Only transport-level structure is enforced here (HTTP 400). Everything the endpoint
/// exists to judge - party size, opening hours - is a 200 with <c>valid=false</c> and a
/// customer-facing message, because those are things the caller can still correct on the
/// phone. The same split <see cref="Cordia.Application.Handlers.Capacity.CheckCandidateAvailability.CheckCandidateAvailabilityValidator"/>
/// makes between "the request is malformed" and "the request is answerable but wrong".
/// </para>
/// </summary>
public sealed class ValidateBookingDraftValidator : AbstractValidator<ValidateBookingDraftQuery>
{
    public ValidateBookingDraftValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Draft).NotNull();

        When(x => x.Draft != null, () =>
        {
            RuleFor(x => x.Draft.Request)
                .NotNull()
                .WithMessage("draft.request is required.");
        });
    }
}
