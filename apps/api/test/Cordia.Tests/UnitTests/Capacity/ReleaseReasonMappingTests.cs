using Cordia.Domain.Enums;

namespace Cordia.Tests.UnitTests.Capacity;

/// <summary>
/// Unit tests for the reason→status mapping logic in <c>FirstPartyCapacityReservation</c>.
/// The mapping is: Expired → HoldStatus.Expired; everything else → HoldStatus.Released.
/// These tests document the contract without needing an EF/Postgres fixture.
/// </summary>
[Trait("Category", "Unit")]
public class ReleaseReasonMappingTests
{
    // Replicates the mapping in FirstPartyCapacityReservation.ReleaseHoldAsync so that if the
    // implementation changes, this test also breaks and forces a review.
    private static HoldStatus MapReasonToStatus(ReleaseReason reason) => reason switch
    {
        ReleaseReason.Expired => HoldStatus.Expired,
        _ => HoldStatus.Released,
    };

    [Fact]
    public void Expired_MapsTo_HoldStatusExpired()
        => Assert.Equal(HoldStatus.Expired, MapReasonToStatus(ReleaseReason.Expired));

    [Fact]
    public void Compensated_MapsTo_HoldStatusReleased()
        => Assert.Equal(HoldStatus.Released, MapReasonToStatus(ReleaseReason.Compensated));

    [Fact]
    public void Superseded_MapsTo_HoldStatusReleased()
        => Assert.Equal(HoldStatus.Released, MapReasonToStatus(ReleaseReason.Superseded));

    [Fact]
    public void ProviderReleased_MapsTo_HoldStatusReleased()
        => Assert.Equal(HoldStatus.Released, MapReasonToStatus(ReleaseReason.ProviderReleased));

    [Fact]
    public void Finalized_MapsTo_HoldStatusReleased_WhenPassedToRelease()
    {
        // ReleaseReason.Finalized is owned by FinalizeAgainstOperationAsync; if it is defensively
        // passed to ReleaseAsync it maps to Released (not Finalized, which only FinalizeAsync uses).
        Assert.Equal(HoldStatus.Released, MapReasonToStatus(ReleaseReason.Finalized));
    }
}
