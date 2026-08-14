using System.Text.Json.Serialization;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Resources.ListStaffResources;

/// <summary>
/// All staff members for the tenant, including inactive ones, so settings can show the full set.
/// Not owner-gated — the schedule and settings both need the list to work from.
/// </summary>
public sealed record ListStaffResourcesQuery(string TenantId) : IRequest<StaffResourceListResult>;

public sealed record StaffResourceListResult(
    [property: JsonPropertyName("resources")] IReadOnlyList<StaffResourceView> Resources);

public sealed record StaffResourceView(
    [property: JsonPropertyName("resource_id")] string ResourceId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("active")] bool Active);

public sealed class ListStaffResourcesValidator : AbstractValidator<ListStaffResourcesQuery>
{
    public ListStaffResourcesValidator() => RuleFor(x => x.TenantId).NotEmpty();
}

public sealed class ListStaffResourcesQueryHandler(IStaffResourceRepository resources)
    : IRequestHandler<ListStaffResourcesQuery, StaffResourceListResult>
{
    public async Task<StaffResourceListResult> Handle(
        ListStaffResourcesQuery request, CancellationToken ct)
    {
        var rows = await resources.ListAsync(request.TenantId, ct);
        return new StaffResourceListResult(
            rows.Select(r => new StaffResourceView(r.ResourceId, r.Name, r.Role, r.Active)).ToList());
    }
}
