using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;

namespace Cordia.Tests.UnitTests.Menu;

/// <summary>
/// Unit tests for <see cref="CatalogStateMachine"/> — the §5.2 catalog lifecycle edge set
/// over the 9-value <see cref="CatalogStatus"/> enum (frozen decision D4; no ExtractFailed).
/// </summary>
[Trait("Category", "Unit")]
public class CatalogStateMachineTests
{
    public static TheoryData<CatalogStatus, CatalogStatus> LegalEdges() => new()
    {
        { CatalogStatus.Uploaded, CatalogStatus.Scanning },
        { CatalogStatus.Scanning, CatalogStatus.Extracting },
        { CatalogStatus.Scanning, CatalogStatus.Quarantined },
        { CatalogStatus.Extracting, CatalogStatus.DraftReady },
        { CatalogStatus.DraftReady, CatalogStatus.InReview },
        { CatalogStatus.InReview, CatalogStatus.Approved },
        { CatalogStatus.Approved, CatalogStatus.Published },
        { CatalogStatus.Published, CatalogStatus.Superseded },
    };

    [Theory]
    [MemberData(nameof(LegalEdges))]
    public void LegalEdge_IsAllowed_AndEnsureDoesNotThrow(CatalogStatus from, CatalogStatus to)
    {
        Assert.True(CatalogStateMachine.IsAllowed(from, to));
        CatalogStateMachine.EnsureAllowed(from, to); // must not throw
    }

    public static TheoryData<CatalogStatus, CatalogStatus> IllegalEdges() => new()
    {
        { CatalogStatus.Uploaded, CatalogStatus.Published },     // cannot skip the pipeline
        { CatalogStatus.Scanning, CatalogStatus.DraftReady },    // must extract first
        { CatalogStatus.DraftReady, CatalogStatus.Published },   // must be reviewed + approved
        { CatalogStatus.InReview, CatalogStatus.Published },     // must be approved first
        { CatalogStatus.Quarantined, CatalogStatus.Extracting }, // terminal
        { CatalogStatus.Superseded, CatalogStatus.Published },   // terminal
        { CatalogStatus.Published, CatalogStatus.Approved },     // no backward edge
    };

    [Theory]
    [MemberData(nameof(IllegalEdges))]
    public void IllegalEdge_IsNotAllowed_AndEnsureThrows(CatalogStatus from, CatalogStatus to)
    {
        Assert.False(CatalogStateMachine.IsAllowed(from, to));
        var ex = Assert.Throws<InvalidCatalogStateTransitionException>(
            () => CatalogStateMachine.EnsureAllowed(from, to));
        Assert.Equal(from, ex.From);
        Assert.Equal(to, ex.To);
    }

    [Theory]
    [InlineData(CatalogStatus.Quarantined)]
    [InlineData(CatalogStatus.Superseded)]
    public void TerminalStates_HaveNoOutgoingEdges(CatalogStatus terminal)
    {
        Assert.DoesNotContain(terminal, CatalogStateMachine.AllowedTransitions.Keys);
        foreach (var to in Enum.GetValues<CatalogStatus>())
            Assert.False(CatalogStateMachine.IsAllowed(terminal, to));
    }
}
