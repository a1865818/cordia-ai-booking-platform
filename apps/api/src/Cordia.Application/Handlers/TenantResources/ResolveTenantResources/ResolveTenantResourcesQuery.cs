using System.Text.Json.Serialization;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.TenantResources.ResolveTenantResources;

/// <summary>
/// Answers "does this tenant have a service/location the customer could have
/// meant by <paramref name="Query"/>?" (agent tool <c>resolve_tenant_resources</c>).
///
/// With no <paramref name="Query"/> it lists what exists, which is what the agent
/// needs to OFFER options. With one, it returns only matches, and the agent
/// captures an identifier only when exactly one comes back.
/// </summary>
public sealed record ResolveTenantResourcesQuery(
    string TenantId,
    string? Kind,
    string? Query) : IRequest<ResolvedResourcesResult>;

/// <summary>One declared service or location (snake_case wire shape).</summary>
public sealed record ResolvedResourceView(
    [property: JsonPropertyName("resource_id")] string ResourceId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases);

/// <summary>
/// Matches for the query, plus whether the answer is unambiguous.
///
/// <c>resolved</c> is the whole point: the agent must not guess between two
/// candidates, and the server is the only side that knows how many there were.
/// </summary>
public sealed record ResolvedResourcesResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("matches")] IReadOnlyList<ResolvedResourceView> Matches,
    [property: JsonPropertyName("resolved")] ResolvedResourceView? Resolved);

/// <summary>Structural rules for the lookup.</summary>
public sealed class ResolveTenantResourcesValidator : AbstractValidator<ResolveTenantResourcesQuery>
{
    /// <summary>The only two kinds a booking payload carries.</summary>
    public static readonly string[] Kinds = ["service", "location"];

    public ResolveTenantResourcesValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.Kind)
            .Must(k => k is null || Kinds.Contains(k))
            .WithMessage("Kind must be 'service' or 'location' when supplied.");
        RuleFor(x => x.Query).MaximumLength(200);
    }
}
