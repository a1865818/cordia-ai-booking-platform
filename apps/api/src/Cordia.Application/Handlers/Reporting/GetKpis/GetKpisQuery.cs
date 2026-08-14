using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Console.GetBookingOverview;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Reporting.GetKpis;

/// <summary>
/// KPI strip over [From, To) plus the equal-length prior window [From - (To-From), From).
///
/// Complements <c>GET /api/reporting/overview</c>: overview still owns the per-day / channel /
/// service breakdowns; this query is the only place an agent-vs-human split is reported, and
/// only when bookings in the window actually carry an attributed source (Inv-16).
/// </summary>
public sealed record GetKpisQuery(
    string TenantId,
    DateTimeOffset From,
    DateTimeOffset To) : IRequest<KpisView>;

public sealed class GetKpisValidator : AbstractValidator<GetKpisQuery>
{
    /// <summary>Same cap as the overview window so the two screens stay interchangeable.</summary>
    public static readonly TimeSpan MaxWindow = GetBookingOverviewValidator.MaxWindow;

    public GetKpisValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();

        RuleFor(x => x.To)
            .GreaterThan(x => x.From)
            .WithMessage("to must be after from.");

        RuleFor(x => x)
            .Must(x => x.To - x.From <= MaxWindow)
            .WithMessage($"the requested window must not exceed {MaxWindow.TotalDays:0} days.");
    }
}

public sealed record KpiMetricView(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] double? Value,
    [property: JsonPropertyName("unit")] string Unit,
    [property: JsonPropertyName("delta")] double? Delta,
    [property: JsonPropertyName("up")] bool? Up,
    [property: JsonPropertyName("available")] bool Available);

public sealed record HandledSplitView(
    [property: JsonPropertyName("ai")] int Ai,
    [property: JsonPropertyName("you")] int You);

public sealed record KpisView(
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To,
    [property: JsonPropertyName("prior_from")] DateTimeOffset PriorFrom,
    [property: JsonPropertyName("prior_to")] DateTimeOffset PriorTo,
    [property: JsonPropertyName("kpis")] IReadOnlyList<KpiMetricView> Kpis,
    [property: JsonPropertyName("handled")] HandledSplitView? Handled);

public sealed class GetKpisQueryHandler(IConsoleReadModel reads)
    : IRequestHandler<GetKpisQuery, KpisView>
{
    public async Task<KpisView> Handle(GetKpisQuery request, CancellationToken ct)
    {
        var duration = request.To - request.From;
        var priorFrom = request.From - duration;
        var priorTo = request.From;

        var current = await reads.GetKpiFactsAsync(request.TenantId, request.From, request.To, ct);
        var prior = await reads.GetKpiFactsAsync(request.TenantId, priorFrom, priorTo, ct);

        return new KpisView(
            request.From,
            request.To,
            priorFrom,
            priorTo,
            [
                CountMetric("bookings", current.Booked, prior.Booked),
                PercentMetric(
                    "noshow",
                    current.KnownAttendance > 0,
                    current.KnownAttendance == 0 ? 0 : 100.0 * current.NoShow / current.KnownAttendance,
                    prior.KnownAttendance > 0,
                    prior.KnownAttendance == 0 ? 0 : 100.0 * prior.NoShow / prior.KnownAttendance,
                    lowerIsBetter: true),
                PercentMetric(
                    "ai",
                    current.Attributed > 0,
                    current.Attributed == 0 ? 0 : 100.0 * current.AgentAttributed / current.Attributed,
                    prior.Attributed > 0,
                    prior.Attributed == 0 ? 0 : 100.0 * prior.AgentAttributed / prior.Attributed,
                    lowerIsBetter: false),
                CountMetric("rev", current.PaidDepositMinor, prior.PaidDepositMinor, unit: "minor"),
            ],
            current.Attributed == 0
                ? null
                : new HandledSplitView(current.AgentAttributed, current.OperatorAttributed));
    }

    private static KpiMetricView CountMetric(
        string key, double current, double prior, string unit = "count")
        => Compare(key, unit, available: true, current, priorAvailable: true, prior, lowerIsBetter: false);

    private static KpiMetricView PercentMetric(
        string key,
        bool available,
        double current,
        bool priorAvailable,
        double prior,
        bool lowerIsBetter)
        => Compare(key, "percent", available, current, priorAvailable, prior, lowerIsBetter);

    private static KpiMetricView Compare(
        string key,
        string unit,
        bool available,
        double current,
        bool priorAvailable,
        double prior,
        bool lowerIsBetter)
    {
        if (!available)
            return new KpiMetricView(key, null, unit, null, null, Available: false);

        if (!priorAvailable)
            return new KpiMetricView(key, current, unit, null, null, Available: true);

        var delta = current - prior;
        var up = lowerIsBetter ? delta < 0 : delta > 0;
        return new KpiMetricView(key, current, unit, delta, up, Available: true);
    }
}
