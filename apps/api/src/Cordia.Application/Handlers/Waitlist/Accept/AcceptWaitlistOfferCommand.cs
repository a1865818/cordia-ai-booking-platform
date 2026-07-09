using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Accept;

/// <summary>
/// Customer acceptance of a live waitlist offer (spec 11 §5.1). Finalizes the temporary hold and
/// confirms the booking. Idempotent (Inv-5): re-accepting an accepted offer is a no-op.
/// </summary>
public sealed record AcceptWaitlistOfferCommand(
    string TenantId,
    string OfferId,
    string Actor) : IRequest<Unit>;
