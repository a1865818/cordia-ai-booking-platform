using System.Buffers.Text;
using System.Text;
using Cordia.Api.Middleware;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Cordia.Domain.TenantResolution;
using Cordia.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Moq;

namespace Cordia.Tests.UnitTests.Middleware;

/// <summary>
/// typ-dispatch for <see cref="TenantContextVerificationMiddleware"/>: <c>opctx+jws</c> binds
/// tenant + actor (and checks revocation); <c>tenantctx+jws</c> binds tenant only.
/// </summary>
[Trait("Category", "Unit")]
public class TenantContextVerificationMiddlewareTests
{
    [Fact]
    public async Task ChannelTenantContextJws_DoesNotPopulateActor()
    {
        var tenantClaims = new TenantContextClaims(
            "tenant-ch", TenantChannel.Web, "ep-1", "cfg-v1", "corr-1", "sess-1",
            [], [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));

        var tenantSigner = new Mock<ITenantContextSigner>();
        tenantSigner.Setup(s => s.Verify(It.IsAny<string>())).Returns(tenantClaims);
        var opSigner = new Mock<IOperatorSessionSigner>(MockBehavior.Strict);
        var sessions = new Mock<IOperatorSessionRepository>(MockBehavior.Strict);

        var tenantContext = new TenantContext();
        var actorContext = new ActorContext();
        var correlation = MockCorrelation();

        var middleware = new TenantContextVerificationMiddleware(_ => Task.CompletedTask);
        var http = new DefaultHttpContext();
        http.Request.Headers[TenantContextVerificationMiddleware.HeaderName] =
            FakeJws("tenantctx+jws");

        await middleware.InvokeAsync(
            http, tenantSigner.Object, opSigner.Object, sessions.Object,
            tenantContext, actorContext, correlation.Object);

        Assert.Equal("tenant-ch", tenantContext.TenantId);
        Assert.Null(actorContext.ActorId);
        Assert.Null(actorContext.ActorRole);
        tenantSigner.Verify(s => s.Verify(It.IsAny<string>()), Times.Once);
        opSigner.Verify(s => s.Verify(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task OperatorContextJws_SetsTenantAndActor_WhenSessionLive()
    {
        var claims = new OperatorSessionClaims(
            "tenant-op", "opr-1", "owner", "opsess-1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(8));

        var tenantSigner = new Mock<ITenantContextSigner>(MockBehavior.Strict);
        var opSigner = new Mock<IOperatorSessionSigner>();
        opSigner.Setup(s => s.Verify(It.IsAny<string>())).Returns(claims);
        var sessions = new Mock<IOperatorSessionRepository>();
        sessions.Setup(s => s.GetAsync("tenant-op", "opsess-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperatorSession
            {
                TenantId = "tenant-op",
                SessionId = "opsess-1",
                OperatorId = "opr-1",
                IssuedAt = claims.IssuedAt,
                ExpiresAt = claims.ExpiresAt,
            });

        var tenantContext = new TenantContext();
        var actorContext = new ActorContext();
        var correlation = MockCorrelation();

        var middleware = new TenantContextVerificationMiddleware(_ => Task.CompletedTask);
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = "/api/consent-policies";
        http.Request.Headers[TenantContextVerificationMiddleware.HeaderName] =
            FakeJws("opctx+jws");

        await middleware.InvokeAsync(
            http, tenantSigner.Object, opSigner.Object, sessions.Object,
            tenantContext, actorContext, correlation.Object);

        Assert.Equal("tenant-op", tenantContext.TenantId);
        Assert.Equal("opr-1", actorContext.ActorId);
        Assert.Equal("owner", actorContext.ActorRole);
        tenantSigner.Verify(s => s.Verify(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task OperatorContextJws_RevokedSession_Throws()
    {
        var claims = new OperatorSessionClaims(
            "tenant-op", "opr-1", "owner", "opsess-1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(8));

        var opSigner = new Mock<IOperatorSessionSigner>();
        opSigner.Setup(s => s.Verify(It.IsAny<string>())).Returns(claims);
        var sessions = new Mock<IOperatorSessionRepository>();
        sessions.Setup(s => s.GetAsync("tenant-op", "opsess-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperatorSession
            {
                TenantId = "tenant-op",
                SessionId = "opsess-1",
                OperatorId = "opr-1",
                IssuedAt = claims.IssuedAt,
                ExpiresAt = claims.ExpiresAt,
                RevokedAt = DateTimeOffset.UtcNow,
            });

        var middleware = new TenantContextVerificationMiddleware(_ => Task.CompletedTask);
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = "/api/auth/session";
        http.Request.Headers[TenantContextVerificationMiddleware.HeaderName] =
            FakeJws("opctx+jws");

        await Assert.ThrowsAsync<TenantContextVerificationException>(() =>
            middleware.InvokeAsync(
                http,
                new Mock<ITenantContextSigner>(MockBehavior.Strict).Object,
                opSigner.Object,
                sessions.Object,
                new TenantContext(),
                new ActorContext(),
                MockCorrelation().Object));
    }

    [Fact]
    public async Task OperatorContextJws_LogoutPath_SkipsRevocationCheck()
    {
        var claims = new OperatorSessionClaims(
            "tenant-op", "opr-1", "owner", "opsess-1",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(8));

        var opSigner = new Mock<IOperatorSessionSigner>();
        opSigner.Setup(s => s.Verify(It.IsAny<string>())).Returns(claims);
        var sessions = new Mock<IOperatorSessionRepository>(MockBehavior.Strict);

        var tenantContext = new TenantContext();
        var actorContext = new ActorContext();

        var middleware = new TenantContextVerificationMiddleware(_ => Task.CompletedTask);
        var http = new DefaultHttpContext();
        http.Request.Method = HttpMethods.Post;
        http.Request.Path = "/api/auth/logout";
        http.Request.Headers[TenantContextVerificationMiddleware.HeaderName] =
            FakeJws("opctx+jws");

        await middleware.InvokeAsync(
            http,
            new Mock<ITenantContextSigner>(MockBehavior.Strict).Object,
            opSigner.Object,
            sessions.Object,
            tenantContext,
            actorContext,
            MockCorrelation().Object);

        Assert.Equal("opr-1", actorContext.ActorId);
        sessions.Verify(
            s => s.GetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task NoHeader_PassesThrough()
    {
        var tenantContext = new TenantContext();
        var actorContext = new ActorContext();
        var nextCalled = false;

        var middleware = new TenantContextVerificationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            new DefaultHttpContext(),
            new Mock<ITenantContextSigner>(MockBehavior.Strict).Object,
            new Mock<IOperatorSessionSigner>(MockBehavior.Strict).Object,
            new Mock<IOperatorSessionRepository>(MockBehavior.Strict).Object,
            tenantContext,
            actorContext,
            MockCorrelation().Object);

        Assert.True(nextCalled);
        Assert.Equal(string.Empty, tenantContext.TenantId);
        Assert.Null(actorContext.ActorId);
    }

    private static Mock<ICorrelationContext> MockCorrelation()
    {
        var mock = new Mock<ICorrelationContext>();
        mock.Setup(c => c.Current).Returns(AmbientCorrelationContext.Empty);
        return mock;
    }

    private static string FakeJws(string typ)
    {
        var header = Base64Url.EncodeToString(
            Encoding.UTF8.GetBytes($"{{\"alg\":\"HS256\",\"typ\":\"{typ}\"}}"));
        return $"{header}.payload.sig";
    }
}
