using FluentValidation;

namespace Cordia.Application.Handlers.ManageBooking.SubmitBookingModification;

public sealed class SubmitBookingModificationValidator : AbstractValidator<SubmitBookingModificationCommand>
{
    public SubmitBookingModificationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.RouteBookingId).NotEmpty();
        RuleFor(x => x.Request).NotNull();
        When(x => x.Request is not null, () =>
        {
            RuleFor(x => x.Request.ExpectedBookingVersion).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Request.OperationId).NotEmpty();
            RuleFor(x => x.Request.ModificationIdempotencyKey).NotEmpty();
            RuleFor(x => x.Request.IntentRevision).GreaterThanOrEqualTo(0);
            RuleFor(x => x.Request.IntentPayloadHash).NotEmpty();
            RuleFor(x => x.Request.GrantId).NotEmpty();
            RuleFor(x => x.Request.Confirmation).NotNull();
        });
    }
}
