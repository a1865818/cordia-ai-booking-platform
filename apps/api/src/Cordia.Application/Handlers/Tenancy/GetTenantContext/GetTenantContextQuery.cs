using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Tenancy.GetOperatingHours;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Tenancy.GetTenantContext;

/// <summary>
/// Backs the foreground tool <c>get_tenant_context</c> (spec 03 §4.3), which
/// <c>load_context</c> calls on EVERY turn of every call.
///
/// The route it calls — <c>GET /api/tenant-context</c> — did not exist. The tool got a 404,
/// mapped it to <c>not_found</c>, and <c>load_context</c> logged a warning and carried on
/// degraded, so the agent has never known anything about the venue it answers the phone for.
/// The worst consequence was not the missing name: without <c>timezone</c> a spoken "seven pm"
/// was recorded as 19:00Z, which is 5am where the venue is.
///
/// Shape is fixed by the agent-side contract (<c>TenantContextView</c>, <c>extra="forbid"</c>),
/// so every field here is required and none may be added without changing that model too.
/// <c>hours</c> was added to both in the same change: adding it to only this side would have
/// made the whole payload unparseable and cost the agent the timezone as well.
/// </summary>
public sealed record GetTenantContextQuery(string TenantId, string PolicyVersion)
    : IRequest<TenantContextResult>;

public sealed record TenantContextResult(
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("policy_version")] string PolicyVersion,
    [property: JsonPropertyName("timezone")] string Timezone,
    [property: JsonPropertyName("supported_operations")] IReadOnlyList<string> SupportedOperations,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("hours")] IReadOnlyList<OperatingHoursDay> Hours);

public sealed class GetTenantContextQueryHandler(ITenantProfileRepository profiles)
    : IRequestHandler<GetTenantContextQuery, TenantContextResult>
{
    /// <summary>
    /// Fallback timezone when the tenant has no profile row.
    ///
    /// UTC, deliberately, and NOT the server's local zone: a server-local default is the bug
    /// this endpoint exists to fix, one layer down — it would silently work on a developer's
    /// machine in the venue's country and silently misbook everywhere else. UTC is at least
    /// wrong in a way that shows up immediately.
    /// </summary>
    public const string DefaultTimezone = "UTC";

    /// <summary>Fallback currency when the tenant has no profile row.</summary>
    public const string DefaultCurrency = "AUD";

    /// <summary>
    /// Reported when this endpoint is not the authority for policy version — which is always,
    /// since the signed TenantContext carries it and the agent reads it from there first.
    /// </summary>
    public const string UnknownPolicyVersion = "unknown";

    /// <summary>Every operation the §8.3 graph can carry to submission.</summary>
    private static readonly string[] Operations = ["create", "reschedule", "modify", "cancel"];

    public async Task<TenantContextResult> Handle(GetTenantContextQuery request, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(request.TenantId, ct);

        return new TenantContextResult(
            request.TenantId,
            request.PolicyVersion,
            Normalize(profile?.Timezone) ?? DefaultTimezone,
            Operations,
            Capabilities(profile),
            Normalize(profile?.Currency) ?? DefaultCurrency,
            // The agent could not answer "what time do you close tonight?" because nothing on
            // this payload said. The console already had the hours; only the agent, which is
            // the thing customers actually ask, was left guessing.
            OperatingHoursDefaults.ParseOrDefault(profile?.HoursJson));
    }

    /// <summary>Treats empty/whitespace as absent so a blank column behaves like a missing row.</summary>
    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> Capabilities(TenantProfile? profile)
    {
        var capabilities = new List<string>();
        if (profile?.WaitlistAutoBook == true)
            capabilities.Add("waitlist_auto_book");
        return capabilities;
    }
}
