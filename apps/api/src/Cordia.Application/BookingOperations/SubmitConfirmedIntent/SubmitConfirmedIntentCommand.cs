using Cordia.Domain.Generated;
using MediatR;

namespace Cordia.Application.BookingOperations.SubmitConfirmedIntent;

/// <summary>
/// MediatR command that carries a validated <see cref="SubmitConfirmedIntentRequest"/> into the
/// acceptance handler (spec 04 §4.3, §10.3).
/// </summary>
public record SubmitConfirmedIntentCommand(
    string TenantId,
    SubmitConfirmedIntentRequest Request) : IRequest<AcceptanceResult>;
