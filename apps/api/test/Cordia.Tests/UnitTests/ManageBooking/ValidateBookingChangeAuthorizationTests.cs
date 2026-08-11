using System.Text.Json;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.ManageBooking.ValidateBookingChange;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Cordia.Tests.UnitTests.BookingOperations;
using Moq;

namespace Cordia.Tests.UnitTests.ManageBooking;

/// <summary>
/// The pre-flight's staff-role door must carry the same LOCK as the mutation it precedes.
///
/// <para><see cref="ValidateBookingChangeQueryHandler"/> accepts a grant OR an asserted staff
/// role, matching <c>SubmitConfirmedIntentCommandHandler</c>. But the acceptance path gates its
/// role branch on <see cref="AssertedActorOptions.AllowAssertedStaffRole"/> and this one did
/// not — so where a deployment had closed the role branch, the pre-flight stayed open: a caller
/// could aim it at any booking id in the tenant and read back <c>stale_version</c> vs <c>ok</c>,
/// an existence-and-version oracle reached with a tenant header and nothing else (Inv-11).</para>
/// </summary>
[Trait("Category", "Unit")]
public class ValidateBookingChangeAuthorizationTests
{
    private const string TenantId = "tenant-a";
    private const string BookingId = "bk_1";
    private const string CustomerId = "cust_1";

    private readonly Mock<IBookingRepository> _bookings = new();
    private readonly Mock<IGrantAuthorizationService> _grants = new();

    public ValidateBookingChangeAuthorizationTests()
        => _bookings
            .Setup(b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Booking
            {
                TenantId = TenantId,
                BookingId = BookingId,
                BookingReference = "CORD-1",
                Version = 3,
                LifecycleStatus = BookingLifecycleStatus.Confirmed,
                CustomerId = CustomerId,
            });

    [Fact]
    public async Task StaffRole_AuthorizesWithoutAGrant_WhenTheAssertedRoleGateIsOpen()
    {
        var handler = new ValidateBookingChangeQueryHandler(
            _bookings.Object, _grants.Object, AssertedActor.Allowed);

        var result = await handler.Handle(Query(grantId: ""), CancellationToken.None);

        Assert.Equal("ok", result.Status);
        _grants.Verify(
            g => g.RequireGrantAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<VerificationActionEnum>(), It.IsAny<string?>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StaffRole_AuthorizesNothing_WhenTheAssertedRoleGateIsClosed()
    {
        // The regression: with the gate closed the role must fall through to the grant check, and
        // a caller presenting no grant is refused rather than served a validation verdict.
        _grants
            .Setup(g => g.RequireGrantAsync(
                TenantId, "", CustomerId, VerificationActionEnum.Modify, BookingId,
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VerificationRequiredException(
                TenantId, CustomerId, VerificationActionEnum.Modify, BookingId));

        var handler = new ValidateBookingChangeQueryHandler(
            _bookings.Object, _grants.Object, AssertedActor.Denied);

        await Assert.ThrowsAsync<VerificationRequiredException>(
            () => handler.Handle(Query(grantId: ""), CancellationToken.None));
    }

    [Fact]
    public async Task AGrant_StillAuthorizes_WhenTheAssertedRoleGateIsClosed()
    {
        // The gate closes the ROLE door only. The agent lane carries a grant and is untouched.
        _grants
            .Setup(g => g.RequireGrantAsync(
                TenantId, "grant_1", CustomerId, VerificationActionEnum.Modify, BookingId,
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VerificationGrant
            {
                TenantId = TenantId,
                GrantId = "grant_1",
                CustomerRef = CustomerId,
                TargetBookingId = BookingId,
                AllowedAction = VerificationActionEnum.Modify,
                VerificationLevel = VerificationLevel.IdentityVerified,
                SessionOrOperationRef = BookingId,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
            });

        var handler = new ValidateBookingChangeQueryHandler(
            _bookings.Object, _grants.Object, AssertedActor.Denied);

        var result = await handler.Handle(Query(grantId: "grant_1"), CancellationToken.None);

        Assert.Equal("ok", result.Status);
    }

    private static ValidateBookingChangeQuery Query(string grantId)
        => new(
            TenantId,
            BookingId,
            BookingId,
            ExpectedBookingVersion: 3,
            RequestedFields: JsonDocument.Parse("""{ "party_size": 3 }""").RootElement,
            GrantId: grantId,
            ActorId: "console-operator",
            ActorRole: "operator");
}
