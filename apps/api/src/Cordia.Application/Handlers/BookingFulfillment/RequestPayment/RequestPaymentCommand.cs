using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.RequestPayment;

/// <summary>
/// Requests payment for a Held operation (spec 06 §6.1, §10).
///
/// If deposit is required: Held → AwaitingPayment + create DepositSession +
///   emit payment.deposit_link.requested.v1 outbox + audit → StepOutcome.Wait.
/// If deposit is not required: Held → Finalizing directly (no-deposit fast path)
///   → StepOutcome.Continue (driver → FinalizeBookingCommand).
/// </summary>
/// <param name="TenantId">Tenant that owns the operation.</param>
/// <param name="OperationId">Operation to request payment for.</param>
public sealed record RequestPaymentCommand(
    string TenantId,
    string OperationId) : IRequest<StepOutcome>;
