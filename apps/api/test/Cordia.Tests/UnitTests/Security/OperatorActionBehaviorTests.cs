using Cordia.Application.Behaviours;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Exceptions;
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
        string TenantId, string ActorId, string ReasonCode,
        string ActorRole = StaffRole.Operator,
        string RequiredRole = StaffRole.Operator) : IRequest<string>, IOperatorActionRequest
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
    [InlineData("", "reason", StaffRole.Operator)]
    [InlineData("ops.jane", "", StaffRole.Operator)]
    [InlineData("", "", StaffRole.Operator)]
    [InlineData("ops.jane", "reason", "")]
    public async Task MissingActorReasonOrRole_FailsClosedBeforeTheHandlerRuns(
        string actor, string reason, string role)
    {
        var behavior = new OperatorActionBehavior<OperatorRequest, string>(_scopeFactory);
        var handlerRan = false;

        // Typed 403 (spec 14 §24.5): missing actor/role/reason is an authorization failure, not a 400.
        await Assert.ThrowsAsync<AuthorizationFailedException>(() => behavior.Handle(
            new OperatorRequest("tenant-a", actor, reason, ActorRole: role),
            _ =>
            {
                handlerRan = true;
                return Task.FromResult("ok");
            },
            CancellationToken.None));

        Assert.False(handlerRan, "the handler must never run without actor + role + reason code");
        // L6: the denial is audited too (completeness parity with the role-mismatch branch),
        // even when the identity is incomplete — the row still ties to tenant + action.
        _audit.Verify(a => a.WriteAsync(
                It.Is<AuditRecord>(r => r.Decision == "deny" && r.Action == "operator.test_action"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── GD-1 role matrix enforcement (spec 14 §24.5, gate decision 2026-07-11) ──

    [Theory]
    [InlineData(StaffRole.Operator, StaffRole.Owner)] // operator cannot take owner actions
    [InlineData("intruder", StaffRole.Operator)]      // unknown role satisfies nothing
    public async Task RoleMismatch_WritesDenyAudit_AndThrows403_BeforeTheHandler(
        string provided, string required)
    {
        var behavior = new OperatorActionBehavior<OperatorRequest, string>(_scopeFactory);
        var handlerRan = false;

        await Assert.ThrowsAsync<AuthorizationFailedException>(() => behavior.Handle(
            new OperatorRequest("tenant-a", "ops.jane", "reason",
                ActorRole: provided, RequiredRole: required),
            _ =>
            {
                handlerRan = true;
                return Task.FromResult("ok");
            },
            CancellationToken.None));

        Assert.False(handlerRan, "the handler must never run on a role mismatch");
        _audit.Verify(a => a.WriteAsync(
                It.Is<AuditRecord>(r =>
                    r.Decision == "deny"
                    && r.ActorId == "ops.jane"
                    && r.Action == "operator.test_action"
                    && r.ReasonCode == "reason"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OwnerSatisfiesOperatorRequirement_HandlerRuns()
    {
        var behavior = new OperatorActionBehavior<OperatorRequest, string>(_scopeFactory);

        var result = await behavior.Handle(
            new OperatorRequest("tenant-a", "boss", "reason",
                ActorRole: StaffRole.Owner, RequiredRole: StaffRole.Operator),
            _ => Task.FromResult("ok"),
            CancellationToken.None);

        Assert.Equal("ok", result);
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
