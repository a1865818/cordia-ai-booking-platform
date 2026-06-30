using Cordia.Domain.Reporting;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Read-only projection that computes the separated booking funnel counts for a tenant
/// over a time window (spec 13 §3, §5.3, §26, Inv-16).
///
/// Strictly read-only: implementations MUST NOT mutate any business state.
/// </summary>
public interface IBookingFunnelReadModel
{
    /// <summary>
    /// Returns the separated booking funnel for <paramref name="tenantId"/> over
    /// [<paramref name="from"/>, <paramref name="to"/>).
    /// </summary>
    /// <param name="tenantId">Tenant scope (Inv-13).</param>
    /// <param name="from">Window start (inclusive).</param>
    /// <param name="to">Window end (exclusive).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<BookingFunnel> GetFunnelAsync(
        string tenantId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);
}
