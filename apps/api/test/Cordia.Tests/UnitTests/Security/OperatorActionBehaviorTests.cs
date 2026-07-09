using Cordia.Application.Behaviours;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Cordia.Tests.UnitTests.Security;

[Trait("Category", "Unit")]
public class OperatorActionBehaviorTests
{
    private readonly Mock<IAuditWriter> _audit = new();
    private readonly IServiceScopeFactory _scopeFactory;

    public OperatorActionBehaviorTests()
    {
        // The behavior resolves IAuditWriter from a FRESH scope for deny
        // audits; back that scope with the mock.
        _scopeFactory = new ServiceCollection()
            .AddScoped(_ => _audit.Object)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }

    private sealed record PlainRequest : IRequest<string>;

    private sealed record OperatorRequest(
        string TenantId, string ActorId, string ReasonCode) : IRequest<string>, IOperatorActionRequest
    {
        public string ActionName => "operator.test_action";

        public string TargetType => "test_target";

        public string TargetId => "target-1";
    }

    private sealed class TargetNotFound(string message) : DomainException(message);

    [Fact]
    public async Task NonOperatorRequest_PassesThroughUntouched()
    {
        var behavior = new OperatorActionBehavior<PlainRequest, string>(_scopeFactory);

        var result = await behavior.Handle(
            new PlainRequest(), _ => Task.FromResult("ok"), CancellationToken.None);

        Assert.Equal("ok", result);
        _audit.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("", "reason")]
    [InlineData("ops.jane", "")]
    [InlineData("", "")]
    public async Task MissingActorOrReason_FailsClosedBeforeTheHandlerRuns(string actor, string reason)
    {
        var behavior = new OperatorActionBehavior<OperatorRequest, string>(_scopeFactory);
        var handlerRan = false;

        await Assert.ThrowsAsync<ValidationException>(() => behavior.Handle(
            new OperatorRequest("tenant-a", actor, reason),
            _ =>
            {
                handlerRan = true;
                return Task.FromResult("ok");
            },
            CancellationToken.None));

        Assert.False(handlerRan, "the handler must never run without actor + reason code");
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Success_DoesNotWriteTheAllowAudit_ThatIsTheHandlersJob()
    {
        var behavior = new OperatorActionBehavior<OperatorRequest, string>(_scopeFactory);

        var result = await behavior.Handle(
            new OperatorRequest("tenant-a", "ops.jane", "reason"),
            _ => Task.FromResult("ok"),
            CancellationToken.None);

        Assert.Equal("ok", result);
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DomainException_WritesDenyAuditAndRethrows()
    {
        var behavior = new OperatorActionBehavior<OperatorRequest, string>(_scopeFactory);

        await Assert.ThrowsAsync<TargetNotFound>(() => behavior.Handle(
            new OperatorRequest("tenant-a", "ops.jane", "reason"),
            _ => throw new TargetNotFound("nope"),
            CancellationToken.None));

        _audit.Verify(a => a.WriteAsync(
                It.Is<AuditRecord>(r =>
                    r.Decision == "deny"
                    && r.ActorType == "operator"
                    && r.ActorId == "ops.jane"
                    && r.Action == "operator.test_action"
                    && r.TargetId == "target-1"
                    && r.ReasonCode == "reason"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UnexpectedException_IsNotAuditedAsDeny_AndPropagates()
    {
        var behavior = new OperatorActionBehavior<OperatorRequest, string>(_scopeFactory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => behavior.Handle(
            new OperatorRequest("tenant-a", "ops.jane", "reason"),
            _ => throw new InvalidOperationException("boom"),
            CancellationToken.None));

        _audit.VerifyNoOtherCalls();
    }
}
