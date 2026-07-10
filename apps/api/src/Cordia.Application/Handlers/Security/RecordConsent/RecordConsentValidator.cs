using FluentValidation;

namespace Cordia.Application.Handlers.Security.RecordConsent;

/// <summary>
/// Sole required-field enforcement for consent recording. The outcome and
/// notice-channel value sets are closed (no free-form strings — avoids the
/// spec-11 C9 defect family of silently-coerced enum inputs).
///
/// <c>Outcome</c> is a string validated by <c>NotEmpty()</c> plus a closed-set
/// <c>Must</c> (granted / declined / not_required / alternative_offered), so an
/// OMITTED or unknown outcome now 400s rather than silently binding to a default
/// — there is no default-binding fail-safe to rely on. <c>RecordingAllowed</c> is
/// independently constrained to granted/not_required outcomes below. Omitted
/// string members are likewise caught by the NotEmpty rules.
/// </summary>
public sealed class RecordConsentValidator : AbstractValidator<RecordConsentCommand>
{
    public RecordConsentValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.SessionId).NotEmpty();
        RuleFor(x => x.Outcome)
            .NotEmpty()
            .Must(o => RecordConsentCommandHandler.OutcomeMap.ContainsKey(o ?? string.Empty))
            .WithMessage("Outcome must be one of: granted, declined, not_required, alternative_offered.");
        RuleFor(x => x.ConsentPolicyVersion).NotEmpty();
        RuleFor(x => x.JurisdictionPolicyId).NotEmpty();
        RuleFor(x => x.CapturedAt)
            .Must(t => t != default)
            .WithMessage("CapturedAt is required.");
        RuleFor(x => x.NoticeChannel)
            .NotEmpty()
            .Must(c => c is "voice" or "web" or "messaging" or "email")
            .WithMessage("NoticeChannel must be one of: voice, web, messaging, email.");
        // A granted/not_required outcome allows recording; declined/alternative never does.
        RuleFor(x => x)
            .Must(x => !x.RecordingAllowed
                || x.Outcome is "granted" or "not_required")
            .WithMessage("RecordingAllowed=true is only valid for granted or not_required outcomes.");
    }
}
