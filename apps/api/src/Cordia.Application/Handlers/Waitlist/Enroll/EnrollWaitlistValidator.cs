using Cordia.Application.Common;
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
        // §5.1 "verified follow-up" — the captured assurance level is stamped verbatim onto the
        // acceptance confirmation, where an unknown value would be silently coerced to Unverified
        // (ConfirmationMappings.MapVerificationLevel). Reject it at the edge instead.
        RuleFor(x => x.VerificationLevel!)
            .Must(ConfirmationMappings.IsKnownVerificationLevel)
            .When(x => !string.IsNullOrWhiteSpace(x.VerificationLevel))
            .WithMessage(
                "verification_level must be one of: unverified, channel_verified, identity_verified, high_assurance.");
        // §20: an AutoBookGrant authorizes SILENT booking/charging — it is valid only with real
        // recorded consent evidence and a coherent constraint scope. Reject junk grants at the
        // edge instead of letting an empty evidence id authorize auto-booking.
        When(x => x.AutoBook is not null, () =>
        {
            RuleFor(x => x.AutoBook!.ConsentEvidenceId).NotEmpty()
                .WithMessage("auto_book.consent_evidence_id is required — silent auto-book needs recorded consent (§20).");
            RuleFor(x => x.AutoBook!.ConstraintTo)
                .GreaterThan(x => x.AutoBook!.ConstraintFrom)
                .WithMessage("auto_book.constraint_to must be after auto_book.constraint_from.");
            RuleFor(x => x.AutoBook!.MaxPartySize).GreaterThan(0);
            RuleFor(x => x.AutoBook!.MaxDepositAmountMinor).GreaterThanOrEqualTo(0);
        });
    }
}
