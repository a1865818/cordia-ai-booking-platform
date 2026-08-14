using System.Text.Json;
using System.Text.Json.Serialization;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Tenancy.GetOperatingHours;

/// <summary>
/// Reads the tenant's seven-day operating hours (docs/backend-gaps.md G5).
///
/// Default when nothing is stored (matches <c>apps/api/scripts/seed-dev-tenant.sql</c>):
/// Monday–Saturday 09:00–18:00 on, Sunday 09:00–18:00 off. A stored row is returned as-is
/// (normalized to Monday-first order). Not owner-gated — the schedule grid needs it.
/// </summary>
public sealed record GetOperatingHoursQuery(string TenantId) : IRequest<OperatingHoursResult>;

public sealed record OperatingHoursDay(
    [property: JsonPropertyName("d")] string D,
    [property: JsonPropertyName("open")] string Open,
    [property: JsonPropertyName("close")] string Close,
    [property: JsonPropertyName("on")] bool On);

public sealed record OperatingHoursResult(
    [property: JsonPropertyName("hours")] IReadOnlyList<OperatingHoursDay> Hours);

public static class OperatingHoursDefaults
{
    public static readonly string[] DaysOfWeek =
        ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Mon–Sat 09:00–18:00 on, Sunday off — same shape as the demo tenant seed.</summary>
    public static IReadOnlyList<OperatingHoursDay> ClosedSundayWeek()
        => DaysOfWeek.Select(d => new OperatingHoursDay(d, "09:00", "18:00", d != "Sunday")).ToList();

    public static IReadOnlyList<OperatingHoursDay> ParseOrDefault(string? hoursJson)
    {
        if (string.IsNullOrWhiteSpace(hoursJson))
            return ClosedSundayWeek();

        try
        {
            var days = JsonSerializer.Deserialize<List<OperatingHoursDay>>(hoursJson, JsonOptions);
            if (days is null || days.Count != 7)
                return ClosedSundayWeek();

            return OrderMondayFirst(days);
        }
        catch (JsonException)
        {
            return ClosedSundayWeek();
        }
    }

    public static IReadOnlyList<OperatingHoursDay> OrderMondayFirst(IEnumerable<OperatingHoursDay> days)
        => DaysOfWeek
            .Select(name => days.First(d => string.Equals(d.D, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    public static string Serialize(IReadOnlyList<OperatingHoursDay> days)
        => JsonSerializer.Serialize(OrderMondayFirst(days), JsonOptions);
}

public sealed class GetOperatingHoursQueryHandler(ITenantProfileRepository profiles)
    : IRequestHandler<GetOperatingHoursQuery, OperatingHoursResult>
{
    public async Task<OperatingHoursResult> Handle(GetOperatingHoursQuery request, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(request.TenantId, ct);
        return new OperatingHoursResult(OperatingHoursDefaults.ParseOrDefault(profile?.HoursJson));
    }
}
