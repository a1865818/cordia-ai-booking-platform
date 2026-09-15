namespace Cordia.Tests.UnitTests.Tenancy;

/// <summary>
/// The scoped tenant is stamped once and cannot be re-pointed (#80, Inv-13). A repeat of the same
/// tenant is tolerated because dead-letter replay legitimately stamps an already-stamped scope.
/// </summary>
[Trait("Category", "Unit")]
public class TenantContextTests
{
    [Fact]
    public void AFreshScope_HasNoTenant()
        => Assert.Equal(string.Empty, new TenantContext().TenantId);

    [Fact]
    public void SetTenant_StampsAnEmptyScope()
    {
        var ctx = new TenantContext();

        ctx.SetTenant("tenant_a");

        Assert.Equal("tenant_a", ctx.TenantId);
    }

    [Fact]
    public void SetTenant_ToleratesTheSameTenantAgain()
    {
        var ctx = new TenantContext("tenant_a");

        ctx.SetTenant("tenant_a");

        Assert.Equal("tenant_a", ctx.TenantId);
    }

    [Fact]
    public void SetTenant_RefusesToRepointTheScopeAtAnotherTenant()
    {
        var ctx = new TenantContext("tenant_a");

        Assert.Throws<InvalidOperationException>(() => ctx.SetTenant("tenant_b"));
        Assert.Equal("tenant_a", ctx.TenantId);
    }

    /// <summary>Tenant ids are opaque; a case variant is a different tenant, not the same one.</summary>
    [Fact]
    public void SetTenant_ComparesOrdinally()
        => Assert.Throws<InvalidOperationException>(() => new TenantContext("tenant_a").SetTenant("TENANT_A"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SetTenant_RefusesABlankTenant(string blank)
        => Assert.Throws<ArgumentException>(() => new TenantContext().SetTenant(blank));
}
