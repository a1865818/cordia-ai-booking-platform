using Cordia.Application.Common.Authorization;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Preorder.ManualResolution;

/// <summary>
/// Operator resolution of a pre-order parked in <c>ManualReview</c>: cancel it (spec 11 §21.4 — the
/// "or cancel" half). Advances <c>ManualReview → FailedFinal</c> and emits the combined failure
/// outcome. Requires an operator actor (spec 08 authz seam); idempotent on an already-terminal saga.
/// </summary>
public sealed record CancelPreorderManualReviewCommand(
    string TenantId,
    string SagaId,
    string OperatorActor,
    string? Reason,
    string ReasonCode,
    string ActorRole) : IRequest<Unit>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => OperatorActor;

    public string RequiredRole => StaffRole.Operator;

    public string ActionName => "preorder.manual_review_cancelled";

    public string TargetType => "preorder_saga";

    public string TargetId => SagaId;
}

/// <summary>Requires the operator actor before cancelling a parked pre-order (§21.4).</summary>
public sealed class CancelPreorderManualReviewValidator : AbstractValidator<CancelPreorderManualReviewCommand>
{
    public CancelPreorderManualReviewValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.SagaId).NotEmpty();
        RuleFor(x => x.OperatorActor).NotEmpty()
            .WithMessage("An operator actor is required to cancel a parked pre-order (§21.4).");
        RuleFor(x => x.ReasonCode).NotEmpty()
            .WithMessage("A reason code is required for operator actions (spec 14 §24.5).");
        RuleFor(x => x.ActorRole).NotEmpty()
            .WithMessage("A staff role is required for operator actions (X-Actor-Role, spec 14 §24.5).");
    }
}
