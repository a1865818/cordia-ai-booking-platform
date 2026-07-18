using Cordia.Application.Handlers.Reporting.GetBookingFunnel;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Read-only product-reporting endpoints (spec 13 §3, §5.3, §26, Inv-16).
///
/// GET api/reporting/funnel?from=...&amp;to=... → <see cref="BookingFunnelView"/> (200 | 400)
///
/// Returns the three separated funnel counts (accepted / confirmed / notified) plus sub-state
/// counts (failedTerminal, ambiguous, notifyPending).  The three primary funnels are NEVER
/// collapsed into a single number (Inv-16, §26).
/// </summary>
[ApiController]
[Route("api/reporting")]
public class ReportingController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>
    /// Returns the separated booking funnel for the current tenant over [from, to).
    ///
    /// If <paramref name="from"/> and <paramref name="to"/> are omitted the endpoint defaults to
    /// the last 24 hours.  Both parameters are ISO-8601 / RFC-3339 DateTimeOffset strings.
    /// </summary>
    /// <param name="from">Window start (inclusive). Default: 24 h ago.</param>
    /// <param name="to">Window end (exclusive). Default: now.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("funnel")]
    [ProducesResponseType(typeof(BookingFunnelView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BookingFunnelView>> GetFunnel(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        var resolvedTo = to ?? DateTimeOffset.UtcNow;
        var resolvedFrom = from ?? resolvedTo.AddHours(-24);

        var view = await mediator.Send(
            new GetBookingFunnelQuery(tenant.TenantId, resolvedFrom, resolvedTo), ct);

        return Ok(view);
    }
}
