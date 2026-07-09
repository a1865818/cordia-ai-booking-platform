using MediatR;

namespace Cordia.Application.Handlers.Waitlist.AutoAccept;

/// <summary>
/// Platform auto-acceptance of a waitlist offer under a recorded <c>AutoBookGrant</c> (spec 11 §5.1,
/// §20). Enqueued by <see cref="Match.MatchWaitlistOfferCommandHandler"/> when a matched slot satisfies
/// the grant's constraints, instead of finalizing the hold + emitting a bespoke event.
///
/// <para><b>Deliberately a plain <see cref="IRequest{T}"/> (NOT an <c>IInboxConsumerRequest</c>).</b>
/// It must mint a spec-04 confirmed revision via <c>SubmitConfirmedIntentCommand</c>, which opens its
/// own <c>UnitOfWork</c> transaction — so this consumer must NOT run inside the inbox transaction
/// (nested <c>BeginTransactionAsync</c> would throw). This mirrors the booking driver
/// (<c>AdvanceBookingOperationCommand</c>), which is also a plain request for the same reason.
/// Idempotency comes from the deterministic spec-04 key <c>wl_accept_{offerId}</c> plus the offer's
/// version-guarded <c>Offered → Accepted</c> claim (Inv-5), not from the inbox.</para>
/// </summary>
public sealed record AutoAcceptWaitlistOfferCommand(
    string TenantId,
    string OfferId) : IRequest<Unit>
{
    /// <summary>Queue message type on the waitlist lane that maps to this command.</summary>
    public const string MessageType = "waitlist.commands.auto_accept.v1";
}
