using Cordia.Application.Common.Authorization;
using Cordia.Domain.Entities;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.ManageBooking;

[Trait("Category", "Unit")]
public class GrantAuthorizationServiceTests
{
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

        var service = new GrantAuthorizationService(repo.Object);

        var result = await service.RequireGrantAsync(
            "tenant-1",
            "grant-1",
            "cust-1",
            VerificationActionEnum.Modify,
            "booking-1",
            TestContext.Current.CancellationToken);

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

        var service = new GrantAuthorizationService(repo.Object);

        var ex = await Assert.ThrowsAsync<VerificationRequiredException>(() =>
            service.RequireGrantAsync(
                "tenant-1",
                "wrong-grant",
                "cust-1",
                VerificationActionEnum.Cancel,
                "booking-1",
                TestContext.Current.CancellationToken));

        Assert.Equal("tenant-1", ex.TenantId);
        Assert.Equal("cust-1", ex.CustomerRef);
        Assert.Equal(VerificationActionEnum.Cancel, ex.Action);
        Assert.Equal("booking-1", ex.TargetBookingId);
        repo.VerifyAll();
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

        var service = new GrantAuthorizationService(repo.Object);

        await Assert.ThrowsAsync<VerificationRequiredException>(() =>
            service.ConsumeGrantAsync(
                "tenant-1",
                "grant-1",
                "cust-1",
                VerificationActionEnum.Modify,
                "booking-1",
                TestContext.Current.CancellationToken));

        repo.VerifyAll();
    }
}
