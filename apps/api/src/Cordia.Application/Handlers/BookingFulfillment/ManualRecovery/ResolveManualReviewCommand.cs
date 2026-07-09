using Cordia.Application.Common.Authorization;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.ManualRecovery;

/// <summary>
/// Human resolution for an operation in ManualReview (spec 06 §11, §17.4, Inv-15).
///
/// <list type="bullet">
///   <item><see cref="ManualReviewResolution.Booked"/>: verified external result — requires a
///   non-empty <see cref="ProviderRef"/>. Transitions ManualReview → Booked, sets BookingId,
///   emits <c>notification.booking_confirmed.v1</c> (Inv-8), closes the open work item.</item>
///   <item><see cref="ManualReviewResolution.FailedFinal"/>: resolved by ops as terminal. Transitions
///   ManualReview → FailedFinal, emits <c>notification.booking_failed.v1</c>, closes the work item.</item>
/// </list>
///
/// Both transitions are §11 legal edges from ManualReview.
/// </summary>
public sealed record ResolveManualReviewCommand(
    string TenantId,
    string OperationId,
    ManualReviewResolution Resolution,
    string? ProviderRef,
    string ReplayCommandId,
    string Actor,
    string ReasonCode) : IRequest<Unit>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string ActionName => "operation.manual_resolved";

    public string TargetType => "booking_operation";

    public string TargetId => OperationId;
}

/// <summary>Requires actor + reason code before a manual resolution (spec 06 Inv-15; spec 14 §24.5).</summary>
public sealed class ResolveManualReviewValidator : AbstractValidator<ResolveManualReviewCommand>
{
    public ResolveManualReviewValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.OperationId).NotEmpty();
        RuleFor(x => x.ReplayCommandId).NotEmpty();
        RuleFor(x => x.Actor).NotEmpty()
            .WithMessage("An operator actor is required to resolve a ManualReview operation (Inv-15).");
        RuleFor(x => x.ReasonCode).NotEmpty()
            .WithMessage("A reason code is required for operator actions (spec 14 §24.5).");
    }
}

/// <summary>Resolution outcomes for a ManualReview booking operation.</summary>
public enum ManualReviewResolution
{
    /// <summary>The booking was confirmed through an external/manual channel.</summary>
    Booked,

    /// <summary>The operation is irrecoverably failed.</summary>
    FailedFinal,
}
