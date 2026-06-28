using Cordia.Application.Handlers.Capacity.ReleaseExpiredHolds;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.Capacity;

/// <summary>
/// Unit tests for <see cref="ReleaseExpiredHoldsCommandHandler"/> (spec 07 T6).
///
/// Verifies routing: hold_id present → <c>ReleaseAsync</c>; hold_id null → <c>ReleaseByOperationAsync</c>.
/// Confirms audit event is emitted in both cases and that the other release method is never called.
/// </summary>
[Trait("Category", "Unit")]
public class ReleaseExpiredHoldsHandlerTests
{
    private readonly Mock<ICapacityReservation> _capacityReservation = new();
    private readonly Mock<IAuditEventRepository> _auditRepo = new();

    private ReleaseExpiredHoldsCommandHandler CreateHandler()
        => new(_capacityReservation.Object, _auditRepo.Object);

    // ── Test 1: HoldId present → ReleaseAsync called; ReleaseByOperationAsync NOT called ──

    /// <summary>
    /// When <see cref="ReleaseExpiredHoldsCommand.HoldId"/> is non-null (scheduled expiry path),
    /// the handler delegates to <c>ICapacityReservation.ReleaseAsync</c> with <c>Expired</c> reason
    /// and never calls <c>ReleaseByOperationAsync</c>.
    /// </summary>
    [Fact]
    public async Task Handle_HoldIdPresent_CallsReleaseAsync_NotReleaseByOperation()
    {
        var tenantId = "t-unit-rel-a";
        var operationId = "op-unit-rel-a";
        var holdId = "hold_abc123";
        var messageId = $"msg-{Guid.NewGuid():N}";

        _capacityReservation
            .Setup(r => r.ReleaseAsync(tenantId, holdId, ReleaseReason.Expired, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _auditRepo
            .Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new ReleaseExpiredHoldsCommand(tenantId, operationId, holdId, messageId);
        var result = await CreateHandler().Handle(cmd, CancellationToken.None);

        Assert.Equal(MediatR.Unit.Value, result);

        // ReleaseAsync called exactly once with correct args and Expired reason.
        _capacityReservation.Verify(
            r => r.ReleaseAsync(tenantId, holdId, ReleaseReason.Expired, It.IsAny<CancellationToken>()),
            Times.Once);

        // ReleaseByOperationAsync must NOT be called.
        _capacityReservation.Verify(
            r => r.ReleaseByOperationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ReleaseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Audit event added.
        _auditRepo.Verify(
            r => r.AddAsync(It.Is<AuditEvent>(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.hold_released" &&
                e.Actor == "system"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Test 2: HoldId null → ReleaseByOperationAsync called; ReleaseAsync NOT called ──

    /// <summary>
    /// When <see cref="ReleaseExpiredHoldsCommand.HoldId"/> is <see langword="null"/>
    /// (deposit-expiry path via <c>capacity.hold_release.requested.v1</c>), the handler
    /// delegates to <c>ICapacityReservation.ReleaseByOperationAsync</c> and never calls
    /// <c>ReleaseAsync</c>.
    /// </summary>
    [Fact]
    public async Task Handle_HoldIdNull_CallsReleaseByOperationAsync_NotReleaseAsync()
    {
        var tenantId = "t-unit-rel-b";
        var operationId = "op-unit-rel-b";
        var messageId = $"msg-{Guid.NewGuid():N}";

        _capacityReservation
            .Setup(r => r.ReleaseByOperationAsync(tenantId, operationId, ReleaseReason.Expired, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _auditRepo
            .Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var cmd = new ReleaseExpiredHoldsCommand(tenantId, operationId, null, messageId);
        var result = await CreateHandler().Handle(cmd, CancellationToken.None);

        Assert.Equal(MediatR.Unit.Value, result);

        // ReleaseByOperationAsync called exactly once with correct args and Expired reason.
        _capacityReservation.Verify(
            r => r.ReleaseByOperationAsync(tenantId, operationId, ReleaseReason.Expired, It.IsAny<CancellationToken>()),
            Times.Once);

        // ReleaseAsync must NOT be called.
        _capacityReservation.Verify(
            r => r.ReleaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ReleaseReason>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Audit event added.
        _auditRepo.Verify(
            r => r.AddAsync(It.Is<AuditEvent>(e =>
                e.TenantId == tenantId &&
                e.OperationId == operationId &&
                e.EventType == "operation.hold_released" &&
                e.Actor == "system"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Test 3: Audit emitted with hold_id in metadata (HoldId present) ──

    [Fact]
    public async Task Handle_HoldIdPresent_AuditMetadataContainsHoldId()
    {
        var tenantId = "t-unit-rel-c";
        var operationId = "op-unit-rel-c";
        var holdId = "hold_meta_test";
        var messageId = $"msg-{Guid.NewGuid():N}";

        _capacityReservation
            .Setup(r => r.ReleaseAsync(tenantId, holdId, ReleaseReason.Expired, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        AuditEvent? capturedAudit = null;
        _auditRepo
            .Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedAudit = evt)
            .Returns(Task.CompletedTask);

        var cmd = new ReleaseExpiredHoldsCommand(tenantId, operationId, holdId, messageId);
        await CreateHandler().Handle(cmd, CancellationToken.None);

        Assert.NotNull(capturedAudit);
        Assert.Contains(holdId, capturedAudit!.Metadata);
        Assert.Contains("expired", capturedAudit.Metadata);
    }

    // ── Test 4: Audit emitted with null hold_id in metadata (HoldId null) ──

    [Fact]
    public async Task Handle_HoldIdNull_AuditMetadataContainsNullHoldId()
    {
        var tenantId = "t-unit-rel-d";
        var operationId = "op-unit-rel-d";
        var messageId = $"msg-{Guid.NewGuid():N}";

        _capacityReservation
            .Setup(r => r.ReleaseByOperationAsync(tenantId, operationId, ReleaseReason.Expired, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        AuditEvent? capturedAudit = null;
        _auditRepo
            .Setup(r => r.AddAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedAudit = evt)
            .Returns(Task.CompletedTask);

        var cmd = new ReleaseExpiredHoldsCommand(tenantId, operationId, null, messageId);
        await CreateHandler().Handle(cmd, CancellationToken.None);

        Assert.NotNull(capturedAudit);
        Assert.Contains("null", capturedAudit!.Metadata); // null hold_id serialized as JSON null
        Assert.Contains("expired", capturedAudit.Metadata);
    }
}
