using System.Text;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Security.GetModelTrace;
using Cordia.Application.Handlers.Security.RecordModelTrace;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.Security;

/// <summary>
/// Model-trace tier (spec 13 §25.3 via the spec-14 boundary): writes re-sanitize content and
/// use the store's short TTL default; views are staff-only (deny audited + 403), and every
/// view — found or not — appends the §4.3 <c>model_trace.viewed</c> audit.
/// </summary>
[Trait("Category", "Unit")]
public class ModelTraceTests
{
    private const string Tenant = "t-1";
    private const string TraceId = "trace-1";

    private readonly Mock<IObjectStore> _store = new();
    private readonly Mock<IAuditWriter> _audit = new();

    [Fact]
    public async Task Record_SanitizesContent_AndStoresUnderTheModelTracePurpose()
    {
        byte[]? stored = null;
        _store.Setup(s => s.PutAsync(Tenant, "model-trace", TraceId,
                It.IsAny<ReadOnlyMemory<byte>>(), null, It.IsAny<CancellationToken>()))
            .Callback<string, string, string, ReadOnlyMemory<byte>, TimeSpan?, CancellationToken>(
                (_, _, _, content, _, _) => stored = content.ToArray())
            .Returns(Task.CompletedTask);
        var handler = new RecordModelTraceCommandHandler(_store.Object);

        var result = await handler.Handle(
            new RecordModelTraceCommand(Tenant, TraceId, "sess-1", "prompt",
                "customer said card 4111111111111111"),
            CancellationToken.None);

        Assert.True(result.Stored);
        Assert.NotNull(stored);
        var json = Encoding.UTF8.GetString(stored);
        Assert.DoesNotContain("4111111111111111", json);
        Assert.Contains("[REDACTED]", json);
    }

    [Theory]
    [InlineData(null, "operator", "why")]      // no actor
    [InlineData("ops.jane", "agent", "why")]   // non-staff role
    [InlineData("ops.jane", "operator", "")]   // no reason code
    public async Task Get_WithoutStaffCredentials_DeniesWithAuditAnd403(
        string? actor, string role, string reason)
    {
        var handler = new GetModelTraceQueryHandler(_store.Object, _audit.Object);

        await Assert.ThrowsAsync<AuthorizationFailedException>(() => handler.Handle(
            new GetModelTraceQuery(Tenant, TraceId, actor, role, reason), CancellationToken.None));

        _audit.Verify(a => a.WriteAsync(
            It.Is<AuditRecord>(r => r.Decision == "deny" && r.Action == "model_trace.viewed"),
            It.IsAny<CancellationToken>()), Times.Once);
        _store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Get_WithOperatorRole_ReturnsTrace_AndAuditsTheView()
    {
        _store.Setup(s => s.GetAsync(Tenant, "model-trace", TraceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes(
                """{"kind":"prompt","session_id":"sess-1","content":"redacted prompt","ts":"2026-07-11T00:00:00+00:00"}"""));
        var handler = new GetModelTraceQueryHandler(_store.Object, _audit.Object);

        var view = await handler.Handle(
            new GetModelTraceQuery(Tenant, TraceId, "ops.jane", StaffRole.Operator, "incident_42"),
            CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal("prompt", view.Kind);
        Assert.Equal("redacted prompt", view.Content);
        _audit.Verify(a => a.WriteAsync(
            It.Is<AuditRecord>(r =>
                r.Decision == "allow"
                && r.Action == "model_trace.viewed"
                && r.ActorId == "ops.jane"
                && r.ReasonCode == "incident_42"
                && r.DataClasses.Contains("model_trace")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Get_UnknownTrace_ReturnsNull_ButStillAuditsTheAttempt()
    {
        _store.Setup(s => s.GetAsync(Tenant, "model-trace", TraceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);
        var handler = new GetModelTraceQueryHandler(_store.Object, _audit.Object);

        var view = await handler.Handle(
            new GetModelTraceQuery(Tenant, TraceId, "boss", StaffRole.Owner, "audit_review"),
            CancellationToken.None);

        Assert.Null(view);
        // §4.3: WHO tried to view is on the trail even when nothing was found.
        _audit.Verify(a => a.WriteAsync(
            It.Is<AuditRecord>(r => r.Decision == "allow" && r.ActorType == StaffRole.Owner),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
