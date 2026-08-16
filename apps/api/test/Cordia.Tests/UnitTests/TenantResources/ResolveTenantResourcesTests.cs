using Cordia.Application.Handlers.TenantResources.ResolveTenantResources;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;

namespace Cordia.Tests.UnitTests.TenantResources;

/// <summary>
/// Unit tests for resolving a spoken name to a declared identifier.
///
/// The interesting assertions are the refusals. The agent may only ever emit an
/// identifier the tenant declared, so an unknown name and an ambiguous one must
/// both produce nothing — a question to the customer is the correct outcome, and
/// a confident wrong answer is the one failure nothing downstream would catch.
/// </summary>
[Trait("Category", "Unit")]
public class ResolveTenantResourcesTests
{
    private sealed class FakeRepository(params TenantResource[] resources) : ITenantResourceRepository
    {
        public Task<IReadOnlyList<TenantResource>> ListAsync(
            string tenantId, string? kind, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TenantResource>>(
                resources.Where(r => r.TenantId == tenantId && r.Active)
                         .Where(r => kind == null || r.Kind == kind)
                         .ToList());
    }

    private static TenantResource Resource(
        string kind, string id, string name, bool active = true, params string[] aliases) =>
        new()
        {
            TenantId = "t1",
            Kind = kind,
            ResourceId = id,
            Name = name,
            Aliases = [.. aliases],
            Active = active,
        };

    private static readonly TenantResource Dinner =
        Resource("service", "dinner", "Dinner sitting", true, "evening", "supper");
    private static readonly TenantResource Lunch =
        Resource("service", "lunch", "Lunch sitting");
    private static readonly TenantResource Fitzroy =
        Resource("location", "fitzroy", "Fitzroy dining room", true, "the fitzroy room");

    private static Task<ResolvedResourcesResult> Resolve(
        string? query, string? kind = null, params TenantResource[] resources)
        => new ResolveTenantResourcesQueryHandler(new FakeRepository(resources))
            .Handle(new ResolveTenantResourcesQuery("t1", kind, query), TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("dinner")]
    [InlineData("Dinner")]
    [InlineData("  DINNER  ")]
    [InlineData("Dinner sitting")]
    [InlineData("evening")]
    public async Task ADeclaredNameResolves(string spoken)
    {
        var result = await Resolve(spoken, null, Dinner, Lunch, Fitzroy);

        Assert.Equal(ResolveTenantResourcesQueryHandler.StatusResolved, result.Status);
        Assert.Equal("dinner", result.Resolved!.ResourceId);
    }

    [Fact]
    public async Task PunctuationAndCasingDoNotDefeatAMatch()
    {
        var result = await Resolve("Fitzroy!", null, Dinner, Fitzroy);

        Assert.Equal("fitzroy", result.Resolved!.ResourceId);
    }

    [Fact]
    public async Task AnAliasPhraseResolves()
    {
        var result = await Resolve("The Fitzroy Room", null, Dinner, Fitzroy);

        Assert.Equal("fitzroy", result.Resolved!.ResourceId);
    }

    [Fact]
    public async Task AnUndeclaredNameResolvesToNothing()
    {
        var result = await Resolve("a haircut", null, Dinner, Lunch, Fitzroy);

        Assert.Equal(ResolveTenantResourcesQueryHandler.StatusNoMatch, result.Status);
        Assert.Null(result.Resolved);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task AnAmbiguousNameReportsAmbiguousRatherThanPicking()
    {
        // Two sittings a venue might both call "evening". Neither may win.
        var lateSupper = Resource("service", "late", "Late supper", true, "evening");

        var result = await Resolve("evening", null, Dinner, lateSupper);

        Assert.Equal(ResolveTenantResourcesQueryHandler.StatusAmbiguous, result.Status);
        Assert.Null(result.Resolved);
        Assert.Equal(2, result.Matches.Count);
    }

    [Fact]
    public async Task APartialWordThatIsNotAPrefixResolvesToNothing()
    {
        // Matching is exact-or-prefix, never "contains": "sitting" is a tail of
        // both "Dinner sitting" and "Lunch sitting" and must not select either.
        // A miss and an ambiguity both end the same way — the agent asks.
        var result = await Resolve("sitting", null, Dinner, Lunch);

        Assert.Equal(ResolveTenantResourcesQueryHandler.StatusNoMatch, result.Status);
        Assert.Null(result.Resolved);
    }

    [Fact]
    public async Task AnExactHitIsNotDilutedByALongerNearMiss()
    {
        var lunchBox = Resource("service", "lunchbox", "Lunch box takeaway");

        var result = await Resolve("lunch", null, Lunch, lunchBox);

        Assert.Equal(ResolveTenantResourcesQueryHandler.StatusResolved, result.Status);
        Assert.Equal("lunch", result.Resolved!.ResourceId);
    }

    [Fact]
    public async Task AnInactiveResourceIsNeverResolved()
    {
        var retired = Resource("service", "highTea", "High tea", active: false);

        var result = await Resolve("high tea", null, retired);

        Assert.Equal(ResolveTenantResourcesQueryHandler.StatusNoMatch, result.Status);
    }

    [Fact]
    public async Task KindNarrowsTheCandidateSet()
    {
        // A room must never resolve against the service list.
        var result = await Resolve("fitzroy", "service", Dinner, Fitzroy);

        Assert.Equal(ResolveTenantResourcesQueryHandler.StatusNoMatch, result.Status);
    }

    [Fact]
    public async Task NoQueryListsWhatExistsSoTheAgentCanOffer()
    {
        var result = await Resolve(null, null, Dinner, Lunch, Fitzroy);

        Assert.Equal(ResolveTenantResourcesQueryHandler.StatusListed, result.Status);
        Assert.Equal(3, result.Matches.Count);
        Assert.Null(result.Resolved);
    }

    [Fact]
    public async Task AWhitespaceOnlyQueryIsTreatedAsNoQuery()
    {
        var result = await Resolve("   ", null, Dinner);

        Assert.Equal(ResolveTenantResourcesQueryHandler.StatusListed, result.Status);
    }

    [Fact]
    public async Task AQueryWithNothingMatchableInItResolvesToNothing()
    {
        // Normalisation strips to alphanumerics, so this reduces to empty — and
        // an empty needle must not match the first resource that also reduces to
        // something short.
        var result = await Resolve("!!!", null, Dinner);

        Assert.Equal(ResolveTenantResourcesQueryHandler.StatusNoMatch, result.Status);
    }

    [Theory]
    [InlineData("", "sess", false)]
    [InlineData("t1", "service", true)]
    [InlineData("t1", "location", true)]
    [InlineData("t1", "aisle", false)]
    public void ValidatorEnforcesTenantAndKind(string tenantId, string kind, bool valid)
    {
        var result = new ResolveTenantResourcesValidator()
            .Validate(new ResolveTenantResourcesQuery(tenantId, kind, "dinner"));

        Assert.Equal(valid, result.IsValid);
    }
}
