using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Cancel;

/// <summary>
/// Cancels a waitlist enrollment (spec 11 §4.4, Inv-6). Terminal + idempotent: re-cancelling a
/// cancelled enrollment is a no-op. Any live offer is released (its temporary hold is rolled back)
/// so the slot is freed.
/// </summary>
public sealed record CancelWaitlistEnrollmentCommand(
    string TenantId,
    string EnrollmentId,
    string Actor) : IRequest<Unit>;
