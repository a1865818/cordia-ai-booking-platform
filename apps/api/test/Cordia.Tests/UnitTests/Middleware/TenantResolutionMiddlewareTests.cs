using Cordia.Api.Middleware;
using Cordia.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;

namespace Cordia.Tests.UnitTests.Middleware;

[Trait("Category", "Unit")]
public class TenantResolutionMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_SetsTeantId_WhenHeaderPresent()
    {
        var tenantContext = new TenantContext();
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new TenantResolutionMiddleware(next);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[TenantResolutionMiddleware.HeaderName] = "tenant-abc";

        await middleware.InvokeAsync(httpContext, tenantContext);

        Assert.Equal("tenant-abc", tenantContext.TenantId);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_LeavesTeantIdEmpty_AndCallsNext_WhenHeaderAbsent()
    {
        var tenantContext = new TenantContext();
        var nextCalled = false;
        RequestDelegate next = _ => { nextCalled = true; return Task.CompletedTask; };
        var middleware = new TenantResolutionMiddleware(next);

        var httpContext = new DefaultHttpContext();
        // No X-Tenant-Id header set.

        await middleware.InvokeAsync(httpContext, tenantContext);

        Assert.Equal(string.Empty, tenantContext.TenantId);
        Assert.True(nextCalled);
    }
}
