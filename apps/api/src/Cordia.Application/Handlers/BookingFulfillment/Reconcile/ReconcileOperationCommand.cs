using Cordia.Application.Handlers.BookingFulfillment;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Reconcile;

/// <summary>
/// Drives the §16.2 reconciliation lookup order (key → reference → status) for an operation
/// currently in <c>Reconciling</c> status (spec 06 §16.2, Inv-7, FM#2).
///
/// Sent by the driver loop when <c>op.Status == Reconciling</c>, and also registered as a
/// consumer message type so the sweeper can enqueue it explicitly.
/// </summary>
public sealed record ReconcileOperationCommand(string TenantId, string OperationId)
    : IRequest<StepOutcome>;
