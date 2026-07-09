using Cordia.Application.Common.Authorization;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Preorder.ManualResolution;

/// <summary>
/// Operator resolution of a pre-order parked in <c>ManualReview</c>: convert it to a standalone order
/// (spec 11 §21.4). A pre-order must not linger after a failed/cancelled booking unless an operator
/// AND the customer explicitly agree to convert it. The customer half is a real, single-use spec-08
/// verification grant (<see cref="GrantId"/>) scoped to the committed booking — not free-text — which
/// the handler consumes (<c>Modify</c> action) against the customer resolved from that booking; the
/// operator half is the audited actor (staff RBAC over <em>which</em> operators may act is the §12
/// open decision, still deferred). HTTP-driven; idempotent on an already-converted saga.
/// </summary>
public sealed record ConvertPreorderToStandaloneCommand(
    string TenantId,
    string SagaId,
    string OperatorActor,
    string GrantId,
    string ReasonCode) : IRequest<Unit>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => OperatorActor;

    public string ActionName => "preorder.standalone_converted";

    public string TargetType => "preorder_saga";

    public string TargetId => SagaId;
}

/// <summary>Requires the operator actor + a customer verification grant (§21.4, spec 08) before conversion.</summary>
public sealed class ConvertPreorderToStandaloneValidator : AbstractValidator<ConvertPreorderToStandaloneCommand>
{
    public ConvertPreorderToStandaloneValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.SagaId).NotEmpty();
        RuleFor(x => x.OperatorActor).NotEmpty()
            .WithMessage("An operator actor is required to convert a pre-order to a standalone order (§21.4).");
        RuleFor(x => x.GrantId).NotEmpty()
            .WithMessage("A customer verification grant is required to convert a pre-order to a standalone order (§21.4, spec 08).");
        RuleFor(x => x.ReasonCode).NotEmpty()
            .WithMessage("A reason code is required for operator actions (spec 14 §24.5).");
    }
}
