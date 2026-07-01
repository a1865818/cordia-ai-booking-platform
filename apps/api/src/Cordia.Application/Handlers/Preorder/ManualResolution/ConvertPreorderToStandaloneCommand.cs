using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Preorder.ManualResolution;

/// <summary>
/// Operator resolution of a pre-order parked in <c>ManualReview</c>: convert it to a standalone order
/// (spec 11 §21.4). A pre-order must not linger after a failed/cancelled booking unless an operator
/// AND the customer explicitly agree to convert it — so both an operator actor and recorded
/// customer-agreement evidence are required, and the action is audited. HTTP-driven (operator action,
/// spec 08 authz seam); idempotent on an already-converted saga.
/// </summary>
public sealed record ConvertPreorderToStandaloneCommand(
    string TenantId,
    string SagaId,
    string OperatorActor,
    string CustomerAgreementEvidenceId) : IRequest<Unit>;

/// <summary>Requires the operator actor + recorded customer agreement (§21.4) before conversion.</summary>
public sealed class ConvertPreorderToStandaloneValidator : AbstractValidator<ConvertPreorderToStandaloneCommand>
{
    public ConvertPreorderToStandaloneValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.SagaId).NotEmpty();
        RuleFor(x => x.OperatorActor).NotEmpty()
            .WithMessage("An operator actor is required to convert a pre-order to a standalone order (§21.4).");
        RuleFor(x => x.CustomerAgreementEvidenceId).NotEmpty()
            .WithMessage("Recorded customer agreement is required to convert a pre-order to a standalone order (§21.4).");
    }
}
