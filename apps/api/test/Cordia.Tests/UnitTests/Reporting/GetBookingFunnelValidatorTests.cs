using Cordia.Application.Handlers.Reporting.GetBookingFunnel;

namespace Cordia.Tests.UnitTests.Reporting;

/// <summary>
/// Unit tests for <see cref="GetBookingFunnelValidator"/> (spec 13 §3).
/// </summary>
[Trait("Category", "Unit")]
public class GetBookingFunnelValidatorTests
{
    private static readonly GetBookingFunnelValidator Validator = new();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Valid_Query_Passes()
    {
        var query = new GetBookingFunnelQuery("tenant-1", Now.AddDays(-1), Now);
        var result = Validator.Validate(query);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void EmptyTenantId_IsRejected()
    {
        var query = new GetBookingFunnelQuery("", Now.AddDays(-1), Now);
        var result = Validator.Validate(query);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == "TenantId");
    }

    [Fact]
    public void WhitespaceTenantId_IsRejected()
    {
        var query = new GetBookingFunnelQuery("   ", Now.AddDays(-1), Now);
        var result = Validator.Validate(query);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void FromEqualTo_IsRejected()
    {
        var query = new GetBookingFunnelQuery("tenant-1", Now, Now);
        var result = Validator.Validate(query);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("From must be earlier than To"));
    }

    [Fact]
    public void FromAfterTo_IsRejected()
    {
        var query = new GetBookingFunnelQuery("tenant-1", Now, Now.AddDays(-1));
        var result = Validator.Validate(query);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void WindowExceeds92Days_IsRejected()
    {
        var query = new GetBookingFunnelQuery("tenant-1", Now.AddDays(-93), Now);
        var result = Validator.Validate(query);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("92"));
    }

    [Fact]
    public void WindowOf92Days_Passes()
    {
        var query = new GetBookingFunnelQuery("tenant-1", Now.AddDays(-92), Now);
        var result = Validator.Validate(query);
        Assert.True(result.IsValid);
    }
}
