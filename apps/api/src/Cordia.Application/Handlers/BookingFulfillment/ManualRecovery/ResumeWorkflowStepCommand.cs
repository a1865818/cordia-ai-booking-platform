using Cordia.Application.Common.Authorization;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.ManualRecovery;

/// <summary>
/// Authorized re-drive of an operation stuck in ManualReview (spec 06 Inv-15, §17.4).
///
/// Transitions ManualReview → Reconciling (the §11 "controlled retry" edge) and emits a
/// <c>booking.command.requested.v1</c> continuation so the workflow driver picks up from the
/// current durable state. The driver routes Reconciling → ReconcileOperationCommand which
/// reconciles before any re-create (Inv-7) using the SAME <c>ProviderIdempotencyKey</c>
/// stored on the step row — the attempt counter is never reset.
///
/// Every replay is audited with its own distinct <see cref="ReplayCommandId"/> (Inv-15).
/// </summary>
public sealed record ResumeWorkflowStepCommand(
    string TenantId,
    string OperationId,
    string ReplayCommandId,
    string Actor,
    string ReasonCode) : IRequest<Unit>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string ActionName => "operation.replay";

    public string TargetType => "booking_operation";

    public string TargetId => OperationId;
}

/// <summary>Requires actor + reason code before an operator re-drive (spec 06 Inv-15; spec 14 §24.5).</summary>
public sealed class ResumeWorkflowStepValidator : AbstractValidator<ResumeWorkflowStepCommand>
{
    public ResumeWorkflowStepValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.OperationId).NotEmpty();
        RuleFor(x => x.ReplayCommandId).NotEmpty();
        RuleFor(x => x.Actor).NotEmpty()
            .WithMessage("An operator actor is required to resume a parked operation (Inv-15).");
        RuleFor(x => x.ReasonCode).NotEmpty()
            .WithMessage("A reason code is required for operator actions (spec 14 §24.5).");
    }
}
