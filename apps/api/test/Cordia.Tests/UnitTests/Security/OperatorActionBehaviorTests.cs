using Cordia.Application.Behaviours;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// A verified operator session — <c>IActorContext.ActorId</c> is non-empty only when
    /// <c>TenantContextVerificationMiddleware</c> bound it from a signature-verified, non-revoked
    /// <c>opctx+jws</c>. Every pre-existing test in this file means "a real operator did this",
    /// so they all take this.
    /// </summary>
    private sealed class ProvenActor : IActorContext
    {
        public string? ActorId => "ops.jane";

        public string? ActorRole => StaffRole.Operator;
    }

    /// <summary>No session was presented; whatever role the request carries came from a header.</summary>
    private sealed class UnprovenActor : IActorContext
    {
        public string? ActorId => null;

        public string? ActorRole => null;
    }

    private OperatorActionBehavior<TReq, string> Behavior<TReq>(
        IActorContext? actor = null, bool allowAsserted = false)
        where TReq : notnull
        => new(_scopeFactory,
               actor ?? new ProvenActor(),
               Options.Create(new AssertedActorOptions { AllowAssertedStaffRole = allowAsserted }));

    /// <summary>Moq expression trees cannot contain <c>?.</c>, so the null check lives here.</summary>
    private static bool ExtraContains(AuditRecord record, string token) =>
        record.Extra is not null
        && record.Extra.ToString()!.Contains(token, StringComparison.Ordinal);

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
        var behavior = Behavior<PlainRequest>();

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
        var behavior = Behavior<OperatorRequest>();
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
        var behavior = Behavior<OperatorRequest>();
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
        var behavior = Behavior<OperatorRequest>();

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
        var behavior = Behavior<OperatorRequest>();

        var result = await behavior.Handle(
            new OperatorRequest("tenant-a", "ops.jane", "reason"),
            _ => Task.FromResult("ok"),
            CancellationToken.None);

        Assert.Equal("ok", result);
        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AuthorizationFailureFromHandler_WritesDenyAuditAndRethrows()
    {
        var behavior = Behavior<OperatorRequest>();

        await Assert.ThrowsAsync<AuthorizationFailedException>(() => behavior.Handle(
            new OperatorRequest("tenant-a", "ops.jane", "reason"),
            _ => throw new AuthorizationFailedException("operator.test_action", "denied inside handler"),
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
    public async Task NonAuthorizationDomainException_IsNotAuditedAsDeny_AndPropagates()
    {
        // B4: a business-rule DomainException (e.g. a 404/409, here a stand-in) is NOT an
        // authorization denial — the narrowed catch must let it through without a DENY row.
        var behavior = Behavior<OperatorRequest>();

        await Assert.ThrowsAsync<TargetNotFound>(() => behavior.Handle(
            new OperatorRequest("tenant-a", "ops.jane", "reason"),
            _ => throw new TargetNotFound("nope"),
            CancellationToken.None));

        _audit.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DenyAudit_IsWrittenOnAnUncanceledToken_EvenWhenTheRequestIsCanceled()
    {
        // B2: a client disconnect cancels the request token, but the DENY record must still persist
        // — the behavior writes the audit on CancellationToken.None, not the request token.
        var behavior = Behavior<OperatorRequest>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<AuthorizationFailedException>(() => behavior.Handle(
            new OperatorRequest("tenant-a", "ops.jane", "reason",
                ActorRole: StaffRole.Operator, RequiredRole: StaffRole.Owner), // role mismatch → deny
            _ => Task.FromResult("ok"),
            cts.Token));

        _audit.Verify(a => a.WriteAsync(
                It.Is<AuditRecord>(r => r.Decision == "deny"),
                It.Is<CancellationToken>(t => !t.IsCancellationRequested)),
            Times.Once);
    }

    [Fact]
    public async Task UnexpectedException_IsNotAuditedAsDeny_AndPropagates()
    {
        var behavior = Behavior<OperatorRequest>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => behavior.Handle(
            new OperatorRequest("tenant-a", "ops.jane", "reason"),
            _ => throw new InvalidOperationException("boom"),
            CancellationToken.None));

        _audit.VerifyNoOtherCalls();
    }

    // ── #72: the role matrix checks what the actor CLAIMS; this checks whether it was PROVEN ──
    //
    // `ActorBinder.Resolve` returns the raw X-Actor / X-Actor-Role headers verbatim whenever no
    // operator session was presented — deliberately, for the dev shim. So before this gate,
    // `X-Actor-Role: owner` plus a reason code satisfied the matrix on every IOperatorActionRequest
    // command in every environment, with no session and no password.

    [Fact]
    public async Task AssertedIdentity_IsRefused_WhenTheGateIsOff()
    {
        var behavior = Behavior<OperatorRequest>(new UnprovenActor(), allowAsserted: false);
        var handlerRan = false;

        var ex = await Assert.ThrowsAsync<AuthorizationFailedException>(() => behavior.Handle(
            // A perfectly well-formed request: identity present, role satisfies, reason given.
            // The ONLY thing wrong with it is that nobody proved who is acting.
            new OperatorRequest("tenant-a", "ops.imposter", "reason", ActorRole: StaffRole.Owner),
            _ => { handlerRan = true; return Task.FromResult("ok"); },
            CancellationToken.None));

        Assert.False(handlerRan);
        Assert.Contains("verified operator session", ex.Message, StringComparison.OrdinalIgnoreCase);

        // The denial is auditable, and says which of the three refusals it was.
        _audit.Verify(a => a.WriteAsync(
            It.Is<AuditRecord>(r => r.Decision == "deny" && ExtraContains(r, "identity_unverified")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The dev shim must keep working, or every local console session and the whole integration
    /// suite dies. `AllowAssertedStaffRole` defaults ON in Development and OFF everywhere else.
    /// </summary>
    [Fact]
    public async Task AssertedIdentity_IsAccepted_WhenTheGateIsExplicitlyOn()
    {
        var behavior = Behavior<OperatorRequest>(new UnprovenActor(), allowAsserted: true);

        var result = await behavior.Handle(
            new OperatorRequest("tenant-a", "ops.jane", "reason"),
            _ => Task.FromResult("ok"), CancellationToken.None);

        Assert.Equal("ok", result);
    }

    /// <summary>
    /// A verified session passes with the gate off — which is the point. This is the paired
    /// negative: without it, a behaviour that refused everything would satisfy the test above.
    /// </summary>
    [Fact]
    public async Task ProvenIdentity_IsAccepted_WithTheGateOff()
    {
        var behavior = Behavior<OperatorRequest>(new ProvenActor(), allowAsserted: false);

        var result = await behavior.Handle(
            new OperatorRequest("tenant-a", "ops.jane", "reason"),
            _ => Task.FromResult("ok"), CancellationToken.None);

        Assert.Equal("ok", result);
    }

    /// <summary>
    /// Ordering: an unproven identity is refused for being unproven, not for its role. A request
    /// whose role would ALSO fail must still report identity_unverified, so the audit trail names
    /// the real reason rather than the first one a reader might assume.
    /// </summary>
    [Fact]
    public async Task AssertedIdentity_IsRefusedForProvenance_NotForRole()
    {
        var behavior = Behavior<OperatorRequest>(new UnprovenActor(), allowAsserted: false);

        await Assert.ThrowsAsync<AuthorizationFailedException>(() => behavior.Handle(
            // operator asserting into an owner-required action: both checks would refuse it.
            new OperatorRequest("tenant-a", "ops.imposter", "reason",
                ActorRole: StaffRole.Operator, RequiredRole: StaffRole.Owner),
            _ => Task.FromResult("ok"), CancellationToken.None));

        _audit.Verify(a => a.WriteAsync(
            It.Is<AuditRecord>(r => ExtraContains(r, "identity_unverified")),
            It.IsAny<CancellationToken>()), Times.Once);
        _audit.Verify(a => a.WriteAsync(
            It.Is<AuditRecord>(r => ExtraContains(r, "role_not_satisfied")),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>A non-operator request is untouched by the gate (Inv-5: consumers stay clear).</summary>
    [Fact]
    public async Task NonOperatorRequest_IsUntouchedByTheProvenanceGate()
    {
        var behavior = Behavior<PlainRequest>(new UnprovenActor(), allowAsserted: false);

        Assert.Equal("ok", await behavior.Handle(
            new PlainRequest(), _ => Task.FromResult("ok"), CancellationToken.None));
        _audit.VerifyNoOtherCalls();
    }
}
