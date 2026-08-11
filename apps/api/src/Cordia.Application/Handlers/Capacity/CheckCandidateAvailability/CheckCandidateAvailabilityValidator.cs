using FluentValidation;

namespace Cordia.Application.Handlers.Capacity.CheckCandidateAvailability;

/// <summary>
/// Structural validator for <see cref="CheckCandidateAvailabilityCommand"/> (spec 03 §4.3).
/// <para>
/// Only transport-level structure is enforced here (HTTP 400). Draft-level problems —
/// a missing <c>starts_at</c>, a TriState sentinel where a value is required — are NOT
/// errors: they are reported back to the agent as <c>invalid_fields</c> inside a 200
/// response so the conversation can recover them (§6.6, Inv-16).
/// </para>
/// </summary>
public sealed class CheckCandidateAvailabilityValidator
    : AbstractValidator<CheckCandidateAvailabilityCommand>
{
    public CheckCandidateAvailabilityValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Request).NotNull();

        When(x => x.Request != null, () =>
        {
            RuleFor(x => x.Request.Draft)
                .NotNull()
                .WithMessage("draft is required.");

            When(x => x.Request.Draft != null, () =>
            {
                RuleFor(x => x.Request.Draft.Request)
                    .NotNull()
                    .WithMessage("draft.request is required.");
            });

            RuleFor(x => x.Request.HoldTtlSeconds)
                .GreaterThan(0)
                .When(x => x.Request.HoldTtlSeconds.HasValue)
                .WithMessage("hold_ttl_seconds must be greater than zero.");

            // A hold is a bounded side effect: it is only idempotent if the caller
            // supplies a stable key (spec 03 §4.3 "Idempotency: when want_hold, the
            // request carries a hold_idempotency_key; repeat returns the same hold").
            RuleFor(x => x.HoldIdempotencyKey)
                .NotEmpty()
                .When(x => x.Request.WantHold == true)
                .WithMessage("hold_idempotency_key is required when want_hold is true.");
        });
    }
}
