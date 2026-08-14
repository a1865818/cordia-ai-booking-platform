using Cordia.Domain.Enums;
using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Console.GetBookingOverview;
using Cordia.Application.Handlers.Reporting.GetBookingFunnel;
using Cordia.Application.Handlers.Reporting.GetKpis;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
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

    /// <summary>
    /// Per-day, per-channel and per-service aggregates over [from, to) for the console's
    /// Analytics screen.
    ///
    /// Complements <c>GET /api/reporting/funnel</c>, which owns the separated funnel counts.
    /// The agent-vs-human split now lives on <c>GET /api/reporting/kpis</c>
    /// (<c>handled</c> / attributed source) and is reported only when bookings in the window
    /// actually carry an attributed source.
    /// </summary>
    /// <param name="from">Window start (inclusive). Default: 7 days ago.</param>
    /// <param name="to">Window end (exclusive). Default: now.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(BookingOverviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BookingOverviewResponse>> GetOverview(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var resolvedTo = to ?? DateTimeOffset.UtcNow;
        var resolvedFrom = from ?? resolvedTo.AddDays(-7);

        var overview = await mediator.Send(
            new GetBookingOverviewQuery(tenant.TenantId, resolvedFrom, resolvedTo), ct);

        return Ok(new BookingOverviewResponse(
            overview.TenantId,
            overview.From,
            overview.To,
            overview.BreakdownTruncated,
            overview.PerDay
                .Select(d => new OverviewDayView(d.Date.ToString("yyyy-MM-dd"), d.Accepted, d.Booked))
                .ToList(),
            overview.Channels
                .Select(c => new OverviewChannelView(EnumWireNames.ToWireName(c.Channel), c.Count))
                .ToList(),
            overview.TopServices
                .Select(s => new OverviewServiceView(s.ServiceId, s.Count))
                .ToList()));
    }

    /// <summary>
    /// KPI strip over [from, to) with an equal-length prior window for deltas.
    ///
    /// Defaults to the last 7 days when omitted — the same default as
    /// <see cref="GetOverview"/>. Window cap matches overview (400 days).
    /// </summary>
    /// <param name="from">Window start (inclusive). Default: 7 days ago.</param>
    /// <param name="to">Window end (exclusive). Default: now.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("kpis")]
    [ProducesResponseType(typeof(KpisView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<KpisView>> GetKpis(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var resolvedTo = to ?? DateTimeOffset.UtcNow;
        var resolvedFrom = from ?? resolvedTo.AddDays(-7);

        return Ok(await mediator.Send(
            new GetKpisQuery(tenant.TenantId, resolvedFrom, resolvedTo), ct));
    }
}

public sealed record OverviewDayView(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("accepted")] int Accepted,
    [property: JsonPropertyName("booked")] int Booked);

public sealed record OverviewChannelView(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("count")] int Count);

public sealed record OverviewServiceView(
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("count")] int Count);

public sealed record BookingOverviewResponse(
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To,
    /// <summary>True when the channel/service breakdown was capped; per-day counts are exact.</summary>
    [property: JsonPropertyName("breakdown_truncated")] bool BreakdownTruncated,
    [property: JsonPropertyName("per_day")] IReadOnlyList<OverviewDayView> PerDay,
    [property: JsonPropertyName("channels")] IReadOnlyList<OverviewChannelView> Channels,
    [property: JsonPropertyName("top_services")] IReadOnlyList<OverviewServiceView> TopServices);
