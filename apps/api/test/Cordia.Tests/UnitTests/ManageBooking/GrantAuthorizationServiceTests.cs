using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Entities;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Cordia.Tests.UnitTests.ManageBooking;

[Trait("Category", "Unit")]
public class GrantAuthorizationServiceTests
{
    private static readonly Mock<IAuditWriter> Audit = new();

    // The service writes deny audits through a fresh DI scope; back it with a mock.
    private static readonly IServiceScopeFactory ScopeFactory = new ServiceCollection()
        .AddScoped(_ => Audit.Object)
        .BuildServiceProvider()
        .GetRequiredService<IServiceScopeFactory>();

    [Fact]
    public async Task RequireGrantAsync_ReturnsPresentedValidGrant()
    {
        var grant = new VerificationGrant
        {
            TenantId = "tenant-1",
            GrantId = "grant-1",
            CustomerRef = "cust-1",
            AllowedAction = VerificationActionEnum.Modify,
            TargetBookingId = "booking-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };
        var repo = new Mock<IVerificationGrantRepository>(MockBehavior.Strict);
        repo.Setup(r => r.FindValidGrantByIdAsync(
                "tenant-1",
                "grant-1",
                "cust-1",
                VerificationActionEnum.Modify,
                "booking-1",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(grant);

        var service = new GrantAuthorizationService(repo.Object, ScopeFactory);

        var result = await service.RequireGrantAsync(
            "tenant-1",
            "grant-1",
            "cust-1",
            VerificationActionEnum.Modify,
            "booking-1",
            ct: TestContext.Current.CancellationToken);

        Assert.Same(grant, result);
        repo.VerifyAll();
    }

    [Fact]
    public async Task RequireGrantAsync_MissingPresentedGrant_ThrowsVerificationRequired()
    {
        var repo = new Mock<IVerificationGrantRepository>(MockBehavior.Strict);
        repo.Setup(r => r.FindValidGrantByIdAsync(
                "tenant-1",
                "wrong-grant",
                "cust-1",
                VerificationActionEnum.Cancel,
                "booking-1",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((VerificationGrant?)null);

        var service = new GrantAuthorizationService(repo.Object, ScopeFactory);

        var ex = await Assert.ThrowsAsync<VerificationRequiredException>(() =>
            service.RequireGrantAsync(
                "tenant-1",
                "wrong-grant",
                "cust-1",
                VerificationActionEnum.Cancel,
                "booking-1",
                ct: TestContext.Current.CancellationToken));

        Assert.Equal("tenant-1", ex.TenantId);
        Assert.Equal("cust-1", ex.CustomerRef);
        Assert.Equal(VerificationActionEnum.Cancel, ex.Action);
        Assert.Equal("booking-1", ex.TargetBookingId);
        repo.VerifyAll();
    }

    [Fact]
    public async Task RequireGrantAsync_DenyAudit_ReflectsPassedTargetType_NotAlwaysBooking()
    {
        // A denied transcript grant (targetBookingId null, from GetTranscript) must be audited as a
        // conversation_session, not a booking — the deny audit's TargetType is now parameterized so it
        // reflects the actual resource being authorized, not a hard-coded "booking".
        var audit = new Mock<IAuditWriter>();
        var scopeFactory = new ServiceCollection()
            .AddScoped(_ => audit.Object)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
        var repo = new Mock<IVerificationGrantRepository>();
        repo.Setup(r => r.FindValidGrantByIdAsync(
                "tenant-1", "grant-x", "cust-1", VerificationActionEnum.ReadSensitive,
                null, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((VerificationGrant?)null);

        var service = new GrantAuthorizationService(repo.Object, scopeFactory);

        await Assert.ThrowsAsync<VerificationRequiredException>(() =>
            service.RequireGrantAsync(
                "tenant-1", "grant-x", "cust-1", VerificationActionEnum.ReadSensitive,
                targetBookingId: null, targetType: "conversation_session",
                ct: TestContext.Current.CancellationToken));

        audit.Verify(a => a.WriteAsync(
            It.Is<AuditRecord>(r =>
                r.Action == "verification.grant_denied"
                && r.Decision == "deny"
                && r.TargetType == "conversation_session"
                && r.TargetId == "grant-x"), // no booking id exists for a transcript read
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConsumeGrantAsync_WhenRepositoryDoesNotConsume_ThrowsVerificationRequired()
    {
        var repo = new Mock<IVerificationGrantRepository>(MockBehavior.Strict);
        repo.Setup(r => r.ConsumeAsync(
                "tenant-1",
                "grant-1",
                "cust-1",
                VerificationActionEnum.Modify,
                "booking-1",
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var service = new GrantAuthorizationService(repo.Object, ScopeFactory);

        await Assert.ThrowsAsync<VerificationRequiredException>(() =>
            service.ConsumeGrantAsync(
                "tenant-1",
                "grant-1",
                "cust-1",
                VerificationActionEnum.Modify,
                "booking-1",
                ct: TestContext.Current.CancellationToken));

        repo.VerifyAll();
    }
}
