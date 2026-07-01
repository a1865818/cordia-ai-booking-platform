using FluentValidation;

namespace Cordia.Application.Handlers.Waitlist.Enroll;

/// <summary>Structural validation for <see cref="EnrollWaitlistCommand"/> (spec 11 §4.4). Failures → HTTP 400.</summary>
public sealed class EnrollWaitlistValidator : AbstractValidator<EnrollWaitlistCommand>
{
    public EnrollWaitlistValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CustomerRef).NotEmpty();
        RuleFor(x => x.LocationId).NotEmpty();
        RuleFor(x => x.ServiceId).NotEmpty();
        RuleFor(x => x.NotificationChannel).NotEmpty();
        RuleFor(x => x.PartySize).GreaterThan(0);
        RuleFor(x => x.OfferLifetimeSeconds).GreaterThan(0);
        RuleFor(x => x.AcceptableTo)
            .GreaterThan(x => x.AcceptableFrom)
            .WithMessage("acceptable_to must be after acceptable_from.");
    }
}
