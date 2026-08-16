using System.Text;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.TenantResources.ResolveTenantResources;

/// <summary>
/// Matches a spoken name against the tenant's declared resources.
///
/// <para><b>Exact after normalisation, not fuzzy.</b> The candidate is compared to
/// the id, the display name and every alias, each reduced to lower-case
/// alphanumerics. "Fitzroy", "fitzroy" and "Fitzroy!" all match the id
/// <c>fitzroy</c>; "the front room" matches only if someone declared it as an
/// alias. That is deliberate — a fuzzy match that is wrong produces a booking
/// against a service the customer never asked for, and unlike a failed match
/// there is nothing downstream to catch it. A miss just asks the customer.</para>
///
/// <para>Trailing filler is tolerated ("dinner sitting" -> <c>dinner</c>) only when
/// exactly one resource's name or id is a whole-word prefix, so ambiguity still
/// reports ambiguous rather than picking one.</para>
/// </summary>
public sealed class ResolveTenantResourcesQueryHandler(ITenantResourceRepository repository)
    : IRequestHandler<ResolveTenantResourcesQuery, ResolvedResourcesResult>
{
    /// <summary>Query matched exactly one resource; <c>resolved</c> is set.</summary>
    public const string StatusResolved = "resolved";

    /// <summary>Query matched more than one; the agent must ask which.</summary>
    public const string StatusAmbiguous = "ambiguous";

    /// <summary>Query matched nothing the tenant has declared.</summary>
    public const string StatusNoMatch = "no_match";

    /// <summary>No query was supplied; <c>matches</c> lists what exists.</summary>
    public const string StatusListed = "listed";

    public async Task<ResolvedResourcesResult> Handle(
        ResolveTenantResourcesQuery query, CancellationToken ct)
    {
        var resources = await repository.ListAsync(query.TenantId, query.Kind, ct);

        if (string.IsNullOrWhiteSpace(query.Query))
            return new ResolvedResourcesResult(StatusListed, resources.Select(View).ToList(), null);

        var needle = Normalise(query.Query);
        if (needle.Length == 0)
            return new ResolvedResourcesResult(StatusNoMatch, [], null);

        var exact = resources.Where(r => Candidates(r).Any(c => c == needle)).ToList();
        // Only fall back to prefix matching when nothing matched outright, so an
        // exact hit is never diluted by a longer near-miss.
        var matches = exact.Count > 0
            ? exact
            : resources.Where(r => Candidates(r).Any(c => c.StartsWith(needle, StringComparison.Ordinal)
                                                          || needle.StartsWith(c, StringComparison.Ordinal))).ToList();

        var views = matches.Select(View).ToList();
        return matches.Count switch
        {
            1 => new ResolvedResourcesResult(StatusResolved, views, views[0]),
            0 => new ResolvedResourcesResult(StatusNoMatch, [], null),
            _ => new ResolvedResourcesResult(StatusAmbiguous, views, null),
        };
    }

    private static ResolvedResourceView View(TenantResource r)
        => new(r.ResourceId, r.Kind, r.Name, r.Aliases);

    private static IEnumerable<string> Candidates(TenantResource r)
    {
        yield return Normalise(r.ResourceId);
        yield return Normalise(r.Name);
        foreach (var alias in r.Aliases)
            yield return Normalise(alias);
    }

    /// <summary>Reduce to lower-case alphanumerics so punctuation and casing cannot miss.</summary>
    private static string Normalise(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
    }
}
