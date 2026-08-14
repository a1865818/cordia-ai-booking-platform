using System.Text.Json.Serialization;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Tenancy.GetTenantProfile;

/// <summary>
/// Reads the tenant business profile (docs/backend-gaps.md G4 / G10). Not owner-gated: the
/// console needs <c>business_type</c> and the venue waitlist switch on every page, and neither
/// is a secret.
///
/// A missing profile row is a real answer, not a 404: <c>business_type</c> is null and
/// <c>waitlist_auto_book</c> is false. <c>display_name</c> is joined from
/// <c>tenant_display_names</c> (or empty) so the shell does not have to call two endpoints.
/// </summary>
public sealed record GetTenantProfileQuery(string TenantId) : IRequest<TenantProfileResult>;

public sealed record TenantProfileResult(
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("business_type")] string? BusinessType,
    [property: JsonPropertyName("waitlist_auto_book")] bool WaitlistAutoBook);

public sealed class GetTenantProfileQueryHandler(
    ITenantProfileRepository profiles,
    ITenantDisplayNameRepository displayNames)
    : IRequestHandler<GetTenantProfileQuery, TenantProfileResult>
{
    public async Task<TenantProfileResult> Handle(GetTenantProfileQuery request, CancellationToken ct)
    {
        var profile = await profiles.GetAsync(request.TenantId, ct);
        var displayName = await displayNames.GetAsync(request.TenantId, ct) ?? string.Empty;

        return new TenantProfileResult(
            request.TenantId,
            displayName,
            profile?.BusinessType,
            profile?.WaitlistAutoBook ?? false);
    }
}
