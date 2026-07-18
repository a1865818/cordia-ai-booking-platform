using MediatR;

namespace Cordia.Application.Handlers.Waitlist.Decline;

/// <summary>
/// Customer decline of a live waitlist offer (spec 11 §5.1). Releases the temporary hold and
/// re-matches the freed slot to the next eligible enrollee. Idempotent: re-declining is a no-op.
/// </summary>
public sealed record DeclineWaitlistOfferCommand(
    string TenantId,
    string OfferId,
    string Actor) : IRequest<Unit>;
