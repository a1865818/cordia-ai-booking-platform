using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Cancellation;

/// <summary>
/// Sets the cancellation-requested flag on a non-terminal booking operation (spec 06 §15.5, FM#13).
///
/// Returns <c>true</c> if the flag was newly set, <c>false</c> if it was already set or
/// the operation was not found / in a terminal state.
///
/// Post-booking cancellation (an already-Booked operation) is out of scope for T8 and will be
/// implemented as a separate new operation in spec 08.
/// </summary>
public sealed record RequestCancellationCommand(string TenantId, string OperationId)
    : IRequest<bool>;
