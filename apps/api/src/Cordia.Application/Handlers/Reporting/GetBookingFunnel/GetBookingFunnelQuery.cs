using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Reporting.GetBookingFunnel;

/// <summary>
/// Query to compute the separated booking funnel for a tenant over a time window
/// (spec 13 §3, §5.3, §26, Inv-16).
/// </summary>
public sealed record GetBookingFunnelQuery(
    string TenantId,
    DateTimeOffset From,
    DateTimeOffset To) : IRequest<BookingFunnelView>;

/// <summary>
/// Read-only projection returned by <see cref="GetBookingFunnelQueryHandler"/>.
///
/// The three primary funnel counts MUST be distinct fields — they MUST NOT be collapsed
/// (spec 13 §2, §5.3, §26, Inv-16).
/// </summary>
public sealed record BookingFunnelView(
    string TenantId,
    DateTimeOffset From,
    DateTimeOffset To,
    /// <summary>Every durable operation accepted in the window (Inv-4).</summary>
    int Accepted,
    /// <summary>Operations that reached Status=Booked (Inv-8 persisted).</summary>
    int Confirmed,
    /// <summary>Booked operations with a Delivered notification (Inv-9). Never exceeds Confirmed.</summary>
    int Notified,
    /// <summary>Operations in a terminal-failure state (Rejected, FailedFinal, Expired).</summary>
    int FailedTerminal,
    /// <summary>Operations in Status=Reconciling — provider outcome unknown (§5.3).</summary>
    int Ambiguous,
    /// <summary>Confirmed − Notified: Booked operations not yet notified.</summary>
    int NotifyPending);

/// <summary>
/// Handles <see cref="GetBookingFunnelQuery"/> by delegating to the
/// <see cref="IBookingFunnelReadModel"/> domain interface (read-only, Inv-16).
/// </summary>
public sealed class GetBookingFunnelQueryHandler(IBookingFunnelReadModel readModel)
    : IRequestHandler<GetBookingFunnelQuery, BookingFunnelView>
{
    public async Task<BookingFunnelView> Handle(GetBookingFunnelQuery request, CancellationToken ct)
    {
        var funnel = await readModel.GetFunnelAsync(request.TenantId, request.From, request.To, ct);

        return new BookingFunnelView(
            TenantId: funnel.TenantId,
            From: funnel.From,
            To: funnel.To,
            Accepted: funnel.Accepted,
            Confirmed: funnel.Confirmed,
            Notified: funnel.Notified,
            FailedTerminal: funnel.FailedTerminal,
            Ambiguous: funnel.Ambiguous,
            NotifyPending: funnel.NotifyPending);
    }
}
