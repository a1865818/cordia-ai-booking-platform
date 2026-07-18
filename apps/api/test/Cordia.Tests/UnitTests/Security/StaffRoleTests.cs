using Cordia.Application.Common.Authorization;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// GD-1 role matrix (spec 14 §24.5/§28.4, gate decision 2026-07-11): owner-required actions
/// demand owner; operator-required actions accept operator or owner; unknown/blank roles and
/// unknown requirements satisfy nothing (fail-closed).
/// </summary>
[Trait("Category", "Unit")]
public class StaffRoleTests
{
    [Theory]
    [InlineData(StaffRole.Owner, StaffRole.Owner, true)]
    [InlineData(StaffRole.Operator, StaffRole.Owner, false)]
    [InlineData(StaffRole.Owner, StaffRole.Operator, true)] // owner ⊇ operator
    [InlineData(StaffRole.Operator, StaffRole.Operator, true)]
    [InlineData("staff", StaffRole.Operator, false)]
    [InlineData("", StaffRole.Operator, false)]
    [InlineData(null, StaffRole.Owner, false)]
    [InlineData(StaffRole.Owner, "unknown_requirement", false)] // fail-closed on the requirement too
    public void Satisfies_FollowsTheGd1Matrix(string? provided, string required, bool expected)
        => Assert.Equal(expected, StaffRole.Satisfies(provided, required));

    [Theory]
    [InlineData(StaffRole.Owner, true)]
    [InlineData(StaffRole.Operator, true)]
    [InlineData("agent", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsKnown_AcceptsOnlyTheRatifiedStaffRoles(string? role, bool expected)
        => Assert.Equal(expected, StaffRole.IsKnown(role));
}
