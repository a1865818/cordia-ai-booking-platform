using Cordia.Domain.Generated;
using FluentValidation;

namespace Cordia.Application.BookingOperations.SubmitConfirmedIntent;

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
    }
}
