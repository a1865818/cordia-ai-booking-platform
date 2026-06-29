using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Sla;

/// <summary>
/// Application-layer implementation of <see cref="ISlaEvaluator"/> that delegates to
/// <see cref="EvaluateOperationSlaCommand"/> via MediatR.
///
/// Registered as Scoped in <c>AddApplication()</c>; resolved by
/// <c>SlaEvaluatorHostedService</c> through the domain interface so Infrastructure
/// does not reference Application directly.
/// </summary>
public sealed class MediatRSlaEvaluator(IMediator mediator) : ISlaEvaluator
{
    public Task EvaluateAsync(string tenantId, string operationId, CancellationToken ct = default)
        => mediator.Send(new EvaluateOperationSlaCommand(tenantId, operationId), ct);
}
