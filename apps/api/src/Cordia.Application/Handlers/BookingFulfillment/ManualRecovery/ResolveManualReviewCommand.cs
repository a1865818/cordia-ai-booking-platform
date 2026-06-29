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
    string Actor) : IRequest<Unit>;

/// <summary>Resolution outcomes for a ManualReview booking operation.</summary>
public enum ManualReviewResolution
{
    /// <summary>The booking was confirmed through an external/manual channel.</summary>
    Booked,

    /// <summary>The operation is irrecoverably failed.</summary>
    FailedFinal,
}
