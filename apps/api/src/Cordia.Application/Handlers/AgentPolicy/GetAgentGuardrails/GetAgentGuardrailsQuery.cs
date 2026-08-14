using System.Text.Json;
using System.Text.Json.Serialization;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.AgentPolicy;

/// <summary>
/// Canonical agent-guardrail ids and console labels (docs/backend-gaps.md G8).
/// Locked rails (g1, g4) are always on in the GET response; a client <c>on: false</c> is ignored.
/// </summary>
public static class AgentGuardrailCatalog
{
    public const string G1 = "g1";
    public const string G5 = "g5";

    public sealed record Definition(string Id, string Label, bool Locked, bool DefaultOn);

    public static readonly IReadOnlyList<Definition> All =
    [
        new(G1, "Never book without reading the details back", Locked: true, DefaultOn: true),
        new("g2", "Require a deposit from customers with a past no-show", Locked: false, DefaultOn: true),
        new("g3", "Escalate to a human when unsure about a service", Locked: false, DefaultOn: true),
        new("g4", "Never send marketing without owner approval", Locked: true, DefaultOn: true),
        new(G5, "Auto-book from waitlist when a slot opens", Locked: false, DefaultOn: true),
        new("g6", "Offer discounts on its own", Locked: false, DefaultOn: false),
    ];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static Dictionary<string, bool> Defaults()
        => All.ToDictionary(d => d.Id, d => d.DefaultOn, StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, bool> ParseOrDefault(string? togglesJson)
    {
        var merged = Defaults();
        if (string.IsNullOrWhiteSpace(togglesJson))
            return ForceLockedOn(merged);

        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, bool>>(togglesJson, JsonOptions);
            if (stored is not null)
            {
                foreach (var def in All)
                {
                    if (stored.TryGetValue(def.Id, out var on))
                        merged[def.Id] = on;
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to defaults.
        }

        return ForceLockedOn(merged);
    }

    public static Dictionary<string, bool> ForceLockedOn(Dictionary<string, bool> toggles)
    {
        foreach (var def in All.Where(d => d.Locked))
            toggles[def.Id] = true;
        return toggles;
    }

    public static string Serialize(IReadOnlyDictionary<string, bool> toggles)
        => JsonSerializer.Serialize(
            All.ToDictionary(d => d.Id, d => toggles.TryGetValue(d.Id, out var on) && on),
            JsonOptions);

    public static IReadOnlyList<GuardrailItem> ToItems(IReadOnlyDictionary<string, bool> toggles)
        => All.Select(d => new GuardrailItem(
            d.Id,
            d.Label,
            d.Locked || (toggles.TryGetValue(d.Id, out var on) && on),
            d.Locked)).ToList();
}

public sealed record GuardrailItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("on")] bool On,
    [property: JsonPropertyName("locked")] bool Locked);

public sealed record AgentGuardrailsResult(
    [property: JsonPropertyName("guardrails")] IReadOnlyList<GuardrailItem> Guardrails);

public sealed record GetAgentGuardrailsQuery(string TenantId) : IRequest<AgentGuardrailsResult>;

public sealed class GetAgentGuardrailsQueryHandler(IAgentGuardrailsRepository repo)
    : IRequestHandler<GetAgentGuardrailsQuery, AgentGuardrailsResult>
{
    public async Task<AgentGuardrailsResult> Handle(GetAgentGuardrailsQuery request, CancellationToken ct)
    {
        var row = await repo.GetAsync(request.TenantId, ct);
        var toggles = AgentGuardrailCatalog.ParseOrDefault(row?.TogglesJson);
        return new AgentGuardrailsResult(AgentGuardrailCatalog.ToItems(toggles));
    }
}
