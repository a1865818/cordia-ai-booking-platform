using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Compensate;

/// <summary>
/// Drives the CompensationRequired → {FailedFinal | ManualReview} transition for a booking
/// operation (spec 06 §9 FM#12, §15.5, §11).
/// </summary>
public sealed record CompensateOperationCommand(string TenantId, string OperationId)
    : IRequest<StepOutcome>;
