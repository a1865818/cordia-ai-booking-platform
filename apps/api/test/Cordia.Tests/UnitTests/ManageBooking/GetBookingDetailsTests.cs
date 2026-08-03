using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.ManageBooking.GetBookingDetails;
using Cordia.Domain.Bookings;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.ManageBooking;

/// <summary>
/// Unit tests for <see cref="GetBookingDetailsQueryHandler"/> (spec 08 §9.4.8).
///
/// The load-bearing case is <see cref="Version_ComesFromTheBookingsRow_NotTheOperation"/>:
/// modify and cancel compare <c>expected_booking_version</c> against
/// <c>bookings.version</c>, while <see cref="BookingProjection.Version"/> is the *operation*
/// version. Returning the latter would hand every client a token guaranteed to 409.
/// </summary>
[Trait("Category", "Unit")]
public class GetBookingDetailsTests
{
    private const string TenantId = "tenant-a";
    private const string BookingId = "bk_1";

    private readonly Mock<IBookingReadRepository> _reads = new();
    private readonly Mock<IBookingRepository> _bookings = new();
    private readonly Mock<IGrantAuthorizationService> _grants = new();
    private readonly Mock<IAuditWriter> _audit = new();

    private GetBookingDetailsQueryHandler CreateHandler()
        => new(_reads.Object, _bookings.Object, _grants.Object, _audit.Object);

    private static BookingProjection Projection(long operationVersion)
        => new(
            BookingId,
            "op_1",
            "CORD-1",
            "Bella Nails",
            new DateOnly(2026, 8, 1),
            "18:30",
            new DateTimeOffset(2026, 8, 1, 18, 30, 0, TimeSpan.Zero),
            4,
            "confirmed",
            OperationStatus.Booked,
            OperationTypeEnum.Create,
            operationVersion,
            DateTimeOffset.UtcNow);

    private static Booking BookingRow(long version, string customerId = "cust_1")
        => new()
        {
            TenantId = TenantId,
            BookingId = BookingId,
            BookingReference = "CORD-1",
            Version = version,
            LifecycleStatus = BookingLifecycleStatus.Confirmed,
            CustomerId = customerId,
        };

    [Fact]
    public async Task Version_ComesFromTheBookingsRow_NotTheOperation()
    {
        // Deliberately different numbers: if the mapper ever reads the projection's
        // version this assertion fails instead of silently shipping the wrong token.
        _reads
            .Setup(r => r.GetBookingProjectionByBookingIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Projection(operationVersion: 7));
        _bookings
            .Setup(b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BookingRow(version: 3));

        var result = await CreateHandler().Handle(
            new GetBookingDetailsQuery(TenantId, BookingId, GrantId: null),
            CancellationToken.None);

        Assert.Equal(GetBookingDetailsStatusEnum.Ok, result.Status);
        Assert.Equal(3, result.Booking!.Version);
    }

    [Fact]
    public async Task Version_IsNull_WhenNoAuthoritativeBookingRowExistsYet()
    {
        _reads
            .Setup(r => r.GetBookingProjectionByBookingIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Projection(operationVersion: 7));
        _bookings
            .Setup(b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Booking?)null);

        var result = await CreateHandler().Handle(
            new GetBookingDetailsQuery(TenantId, BookingId, GrantId: null),
            CancellationToken.None);

        Assert.Equal(GetBookingDetailsStatusEnum.Ok, result.Status);
        Assert.Null(result.Booking!.Version);
    }

    [Fact]
    public async Task NotFound_WhenNoProjectionExists()
    {
        _reads
            .Setup(r => r.GetBookingProjectionByBookingIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingProjection?)null);

        var result = await CreateHandler().Handle(
            new GetBookingDetailsQuery(TenantId, BookingId, GrantId: null),
            CancellationToken.None);

        Assert.Equal(GetBookingDetailsStatusEnum.NotFound, result.Status);
        Assert.Null(result.Booking);
        _bookings.Verify(
            b => b.GetByIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RequiresVerification_WhenGrantSuppliedButNoAuthoritativeRow()
    {
        _reads
            .Setup(r => r.GetBookingProjectionByBookingIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Projection(operationVersion: 7));
        _bookings
            .Setup(b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Booking?)null);

        var result = await CreateHandler().Handle(
            new GetBookingDetailsQuery(TenantId, BookingId, GrantId: "grant_1"),
            CancellationToken.None);

        Assert.Equal(GetBookingDetailsStatusEnum.RequiresVerification, result.Status);
    }

    [Fact]
    public async Task RequiresVerification_WhenGrantIsRejected()
    {
        _reads
            .Setup(r => r.GetBookingProjectionByBookingIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Projection(operationVersion: 7));
        _bookings
            .Setup(b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BookingRow(version: 3));
        _grants
            .Setup(g => g.RequireGrantAsync(
                TenantId,
                "grant_1",
                "cust_1",
                VerificationActionEnum.ReadSensitive,
                BookingId,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new VerificationRequiredException(
                TenantId,
                "cust_1",
                VerificationActionEnum.ReadSensitive,
                BookingId));

        var result = await CreateHandler().Handle(
            new GetBookingDetailsQuery(TenantId, BookingId, GrantId: "grant_1"),
            CancellationToken.None);

        Assert.Equal(GetBookingDetailsStatusEnum.RequiresVerification, result.Status);
        // The minimal projection is still returned, and still carries the booking version.
        Assert.Equal(3, result.Booking!.Version);
        _audit.Verify(
            a => a.WriteAsync(It.IsAny<AuditRecord>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SensitiveRead_IsAudited_WhenGrantIsAccepted()
    {
        _reads
            .Setup(r => r.GetBookingProjectionByBookingIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Projection(operationVersion: 7));
        _bookings
            .Setup(b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BookingRow(version: 3));

        var result = await CreateHandler().Handle(
            new GetBookingDetailsQuery(TenantId, BookingId, GrantId: "grant_1"),
            CancellationToken.None);

        Assert.Equal(GetBookingDetailsStatusEnum.Ok, result.Status);
        Assert.Equal(3, result.Booking!.Version);
        _audit.Verify(
            a => a.WriteAsync(
                It.Is<AuditRecord>(r => r.Action == "read_sensitive_booking" && r.Decision == "allow"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ReadsAreScopedToTheRequestedTenant()
    {
        _reads
            .Setup(r => r.GetBookingProjectionByBookingIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Projection(operationVersion: 7));
        _bookings
            .Setup(b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(BookingRow(version: 3));

        await CreateHandler().Handle(
            new GetBookingDetailsQuery(TenantId, BookingId, GrantId: null),
            CancellationToken.None);

        _reads.Verify(
            r => r.GetBookingProjectionByBookingIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()),
            Times.Once);
        _bookings.Verify(
            b => b.GetByIdAsync(TenantId, BookingId, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
