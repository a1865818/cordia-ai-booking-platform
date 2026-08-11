using Cordia.Application.Common;
using Cordia.Domain.Generated;
using FluentValidation;

namespace Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;

/// <summary>
/// Structural validator for <see cref="SubmitConfirmedIntentCommand"/> (spec 04 §10.3 / §7 Inv-1).
/// All checks here are structural (field presence, cross-field binding) and produce HTTP 400.
/// Semantic failures (hash mismatch, policy violations) are thrown as typed domain exceptions
/// in the handler and map to HTTP 422 via <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class SubmitConfirmedIntentValidator : AbstractValidator<SubmitConfirmedIntentCommand>
{
    public SubmitConfirmedIntentValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.Request).NotNull();

        When(x => x.Request != null, () =>
        {
            RuleFor(x => x.Request.OperationId).NotEmpty();
            RuleFor(x => x.Request.IdempotencyKey).NotEmpty();
            RuleFor(x => x.Request.Revision).NotNull();
            RuleFor(x => x.Request.Confirmation).NotNull();

            // Revision structural checks.
            When(x => x.Request.Revision != null, () =>
            {
                RuleFor(x => x.Request.Revision.IntentId).NotEmpty();
                RuleFor(x => x.Request.Revision.SchemaVersion).NotEmpty();
                RuleFor(x => x.Request.Revision.TenantPolicyVersion).NotEmpty();
                RuleFor(x => x.Request.Revision.CanonicalPayload).NotNull();
                RuleFor(x => x.Request.Revision.PayloadHash)
                    .NotEmpty()
                    .WithMessage("revision.payload_hash is required.");
                RuleFor(x => x.Request.Revision.SourceSessionId)
                    .NotEmpty()
                    .WithMessage("revision.source_session_id is required.");

                // modify / cancel / reschedule require a target_booking_id (Inv-10).
                When(x => x.Request.Revision.CanonicalPayload != null &&
                          x.Request.Revision.CanonicalPayload.Operation is
                              OperationTypeEnum.Modify or
                              OperationTypeEnum.Cancel or
                              OperationTypeEnum.Reschedule,
                    () =>
                    {
                        RuleFor(x => x.Request.Revision.CanonicalPayload.TargetBookingId)
                            .NotEmpty()
                            .WithMessage("target_booking_id is required for modify, cancel, and reschedule operations.");
                    });
            });

            // Confirmation structural checks (Inv-1 evidence fields).
            When(x => x.Request.Confirmation != null, () =>
            {
                RuleFor(x => x.Request.Confirmation.ConfirmationId)
                    .NotEmpty()
                    .WithMessage("confirmation.confirmation_id is required.");
                RuleFor(x => x.Request.Confirmation.IntentPayloadHash)
                    .NotEmpty()
                    .WithMessage("confirmation.intent_payload_hash is required.");
                RuleFor(x => x.Request.Confirmation.Identity)
                    .NotEmpty()
                    .WithMessage("confirmation.identity is required.");
                RuleFor(x => x.Request.Confirmation.SourceTurnId)
                    .NotEmpty()
                    .WithMessage("confirmation.source_turn_id is required.");
                RuleFor(x => x.Request.Confirmation.ReadBackVersion)
                    .NotEmpty()
                    .WithMessage("confirmation.read_back_version is required.");
                // NotEmpty is not enough. MapVerificationLevel falls through to Unverified for
                // anything it does not recognise, so an off-vocabulary value was silently
                // DOWNGRADED — the platform recorded "unverified" for a customer a lane had
                // actually identity-verified, and nothing anywhere said so (R15). The agent lane
                // was sending "none", which is not a member, so every agent submission landed
                // unverified. Reject it up front instead, exactly as the waitlist enrol
                // validator already does.
                RuleFor(x => x.Request.Confirmation.VerificationLevel)
                    .NotEmpty()
                    .WithMessage("confirmation.verification_level is required.")
                    .Must(ConfirmationMappings.IsKnownVerificationLevel)
                    // ApplyConditionTo.CurrentValidator is load-bearing: the DEFAULT applies a
                    // condition to every validator earlier in the chain, so a bare .When() here
                    // silently disabled the NotEmpty() above and an empty value stopped being a
                    // 400 — the opposite of what this rule is for.
                    .When(
                        x => !string.IsNullOrWhiteSpace(x.Request.Confirmation.VerificationLevel),
                        ApplyConditionTo.CurrentValidator)
                    .WithMessage(
                        "confirmation.verification_level must be one of: unverified, "
                        + "channel_verified, identity_verified, high_assurance.");
            });

            // Confirmation structural binding: confirmation must be bound to the same
            // intent_id and revision number as the supplied revision (§8.8, Inv-1).
            When(x => x.Request.Revision != null && x.Request.Confirmation != null, () =>
            {
                RuleFor(x => x.Request.Confirmation.IntentId)
                    .Equal(x => x.Request.Revision.IntentId)
                    .WithMessage("confirmation.intent_id must match revision.intent_id.");

                RuleFor(x => x.Request.Confirmation.IntentRevision)
                    .Equal(x => x.Request.Revision.Revision)
                    .WithMessage("confirmation.intent_revision must match revision.revision.");
            });
        });
    }
}
