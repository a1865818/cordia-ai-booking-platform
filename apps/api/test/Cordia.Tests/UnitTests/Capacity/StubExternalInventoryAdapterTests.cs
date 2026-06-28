using Cordia.Domain.Capacity;
using Cordia.Domain.Enums;
using Cordia.Infrastructure.Capacity.Providers;

#pragma warning disable xUnit1051 // Use TestContext.Current.CancellationToken

namespace Cordia.Tests.UnitTests.Capacity;

/// <summary>
/// Unit tests for <see cref="StubExternalInventoryAdapter"/> (spec 07 §6.3, T4).
/// Verifies adapter metadata and the synthetic hold shape returned by ReserveAsync.
/// No database or external I/O is involved.
/// </summary>
[Trait("Category", "Unit")]
public class StubExternalInventoryAdapterTests
{
    private readonly StubExternalInventoryAdapter _sut = new();

    // -----------------------------------------------------------------------
    // Adapter metadata
    // -----------------------------------------------------------------------

    [Fact]
    public void ProviderName_IsStubExternal()
        => Assert.Equal("stub_external", _sut.ProviderName);

    [Fact]
    public void SupportedPrimitive_IsNativeHold()
        => Assert.Equal(CapacityPrimitive.NativeHold, _sut.SupportedPrimitive);

    // -----------------------------------------------------------------------
    // ReserveAsync — synthetic Held outcome
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReserveAsync_ReturnsHeldStatus()
    {
        var req = BuildRequest();

        var outcome = await _sut.ReserveAsync(req);

        Assert.Equal(ReservationStatus.Held, outcome.Status);
    }

    [Fact]
    public async Task ReserveAsync_HoldHasNonNullProviderRef()
    {
        var req = BuildRequest();

        var outcome = await _sut.ReserveAsync(req);

        Assert.NotNull(outcome.Hold);
        Assert.NotNull(outcome.Hold!.ProviderRef);
        Assert.NotEmpty(outcome.Hold.ProviderRef!);
    }

    [Fact]
    public async Task ReserveAsync_HoldHasNullSlotId()
    {
        var req = BuildRequest();

        var outcome = await _sut.ReserveAsync(req);

        Assert.Null(outcome.Hold!.SlotId);
    }

    [Fact]
    public async Task ReserveAsync_HoldReservedCapacityMatchesRequest()
    {
        var req = BuildRequest(requestedCapacity: 3);

        var outcome = await _sut.ReserveAsync(req);

        Assert.Equal(3, outcome.Hold!.ReservedCapacity);
    }

    [Fact]
    public async Task ReserveAsync_HoldStatusIsActive()
    {
        var req = BuildRequest();

        var outcome = await _sut.ReserveAsync(req);

        Assert.Equal(HoldStatus.Active, outcome.Hold!.Status);
    }

    [Fact]
    public async Task ReserveAsync_HoldPreservesRequestFields()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var req = BuildRequest(
            tenantId: "tenant-abc",
            operationId: "op-xyz",
            workflowStep: "create",
            expiresAt: expiresAt);

        var outcome = await _sut.ReserveAsync(req);
        var hold = outcome.Hold!;

        Assert.Equal("tenant-abc", hold.TenantId);
        Assert.Equal("op-xyz", hold.OperationId);
        Assert.Equal("create", hold.WorkflowStep);
        Assert.Equal(expiresAt, hold.ExpiresAt);
    }

    [Fact]
    public async Task ReserveAsync_HoldVersionIsZero()
    {
        var req = BuildRequest();

        var outcome = await _sut.ReserveAsync(req);

        Assert.Equal(0L, outcome.Hold!.Version);
    }

    [Fact]
    public async Task ReserveAsync_TwoCallsProduce_DifferentProviderRefs()
    {
        var req = BuildRequest();

        var outcome1 = await _sut.ReserveAsync(req);
        var outcome2 = await _sut.ReserveAsync(req);

        Assert.NotEqual(outcome1.Hold!.ProviderRef, outcome2.Hold!.ProviderRef);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static ReserveRequest BuildRequest(
        string tenantId = "tenant-1",
        string operationId = "op-1",
        string workflowStep = "create",
        int requestedCapacity = 2,
        DateTimeOffset expiresAt = default)
    {
        if (expiresAt == default)
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);

        return new ReserveRequest(
            TenantId: tenantId,
            OperationId: operationId,
            WorkflowStep: workflowStep,
            SlotId: null,
            RequestedCapacity: requestedCapacity,
            ExpiresAt: expiresAt,
            ProviderName: "stub_external");
    }
}
