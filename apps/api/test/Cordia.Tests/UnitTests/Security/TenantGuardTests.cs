using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// <see cref="TenantGuard.RequireTenant"/> is the single fail-closed tenant-scope check the
/// controllers share (spec 14 §24.1, Inv-11): empty/whitespace tenant →
/// <see cref="TenantScopeMissingException"/> (mapped to the 400 "Missing tenant" problem).
/// </summary>
[Trait("Category", "Unit")]
public class TenantGuardTests
{
    private sealed record FixedTenant(string TenantId) : ITenantContext;

    [Fact]
    public void ResolvedTenant_ReturnsTheId()
        => Assert.Equal("t-1", TenantGuard.RequireTenant(new FixedTenant("t-1")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingTenant_ThrowsTenantScopeMissing(string tenantId)
    {
        var ex = Assert.Throws<TenantScopeMissingException>(
            () => TenantGuard.RequireTenant(new FixedTenant(tenantId)));
        Assert.Equal("X-Tenant-Id header is required.", ex.Message);
    }
}
