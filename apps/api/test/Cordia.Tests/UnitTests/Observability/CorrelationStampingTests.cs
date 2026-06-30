using Cordia.Domain.Entities;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.EntityFramework.Interceptors;

namespace Cordia.Tests.UnitTests.Observability;

/// <summary>
/// Unit tests for <see cref="CorrelationStampingInterceptor.Stamp"/> covering all 6 §25.1
/// chain entities and the fail-open/no-overwrite invariants (spec 13 §4.1, §8).
/// </summary>
[Trait("Category", "Unit")]
public class CorrelationStampingTests
{
    private static AmbientCorrelationContext MakeCtx(string correlationId = "corr_test", string? causationId = "caus_test")
        => new(TenantId: "tenant-1", CorrelationId: correlationId, CausationId: causationId);

    private static IEnumerable<ICorrelatable> AllSixEntities() =>
    [
        new BookingOperation(),
        new IntentConfirmation(),
        new Notification(),
        new ScheduledJob(),
        new WorkflowAttempt(),
        new OperationWorkItem(),
    ];

    // -----------------------------------------------------------------------
    // Happy path: ambient context stamps all 6 entity types.
    // -----------------------------------------------------------------------

    [Fact]
    public void Stamp_WithAmbientContext_StampsAllSixEntities()
    {
        var ctx = MakeCtx();
        var entities = AllSixEntities().ToList();

        CorrelationStampingInterceptor.Stamp(entities, ctx);

        foreach (var entity in entities)
        {
            Assert.Equal("corr_test", entity.CorrelationId);
            Assert.Equal("caus_test", entity.CausationId);
        }
    }

    // -----------------------------------------------------------------------
    // No-overwrite: a pre-set CorrelationId must NOT be replaced.
    // -----------------------------------------------------------------------

    [Fact]
    public void Stamp_PreSetCorrelationId_IsNotOverwritten()
    {
        var ctx = MakeCtx("corr_ambient");
        var entity = new BookingOperation { CorrelationId = "corr_original" };

        CorrelationStampingInterceptor.Stamp([entity], ctx);

        Assert.Equal("corr_original", entity.CorrelationId);
    }

    [Fact]
    public void Stamp_PreSetCausationId_IsNotOverwritten()
    {
        var ctx = MakeCtx(causationId: "caus_ambient");
        var entity = new BookingOperation { CausationId = "caus_original" };

        CorrelationStampingInterceptor.Stamp([entity], ctx);

        Assert.Equal("caus_original", entity.CausationId);
    }

    // -----------------------------------------------------------------------
    // Fail-open: empty ambient context stamps nothing (no throw).
    // -----------------------------------------------------------------------

    [Fact]
    public void Stamp_EmptyAmbientCorrelationId_StampsNothing()
    {
        var ctx = AmbientCorrelationContext.Empty;
        var entities = AllSixEntities().ToList();

        // Must not throw.
        CorrelationStampingInterceptor.Stamp(entities, ctx);

        foreach (var entity in entities)
        {
            Assert.Null(entity.CorrelationId);
            Assert.Null(entity.CausationId);
        }
    }
}
