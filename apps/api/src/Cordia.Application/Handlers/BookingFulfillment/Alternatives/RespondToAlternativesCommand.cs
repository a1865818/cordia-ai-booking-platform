using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Alternatives;

/// <summary>
/// The customer's response to an <c>AlternativesRequired</c> operation (spec 06 §6.1, T11a).
/// </summary>
public enum AlternativesResponse
{
    /// <summary>Customer accepts the waitlist offer → <c>AlternativesRequired→Waitlisted</c>.</summary>
    Waitlist,

    /// <summary>Customer declines all alternatives → <c>AlternativesRequired→FailedFinal</c>.</summary>
    Decline,
}

/// <summary>
/// Records the customer's response to an alternatives offer (spec 06 §6.1 §11 legal edges).
///
/// <c>Waitlist</c> → <c>AlternativesRequired→Waitlisted</c> + <c>notification.waitlisted.v1</c> + audit.
/// <c>Decline</c>  → <c>AlternativesRequired→FailedFinal</c> + <c>notification.booking_failed.v1</c> + audit.
/// </summary>
public sealed record RespondToAlternativesCommand(
    string TenantId,
    string OperationId,
    AlternativesResponse Response) : IRequest<Unit>;
