using FluentValidation;

namespace Cordia.Application.Handlers.ManageBooking.SubmitBookingCancellation;

public sealed class SubmitBookingCancellationValidator : AbstractValidator<SubmitBookingCancellationCommand>
{
    public SubmitBookingCancellationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.RouteBookingId).NotEmpty();
        RuleFor(x => x.Request).NotNull();
        RuleFor(x => x.Request.TargetBookingId).NotEmpty();
        RuleFor(x => x.Request.ExpectedBookingVersion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Request.OperationId).NotEmpty();
        RuleFor(x => x.Request.CancellationIdempotencyKey).NotEmpty();
        RuleFor(x => x.Request.ReasonCode).NotEmpty();
        RuleFor(x => x.Request.GrantId).NotEmpty();
    }
}
