using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.ManageBooking.RecordAttendance;

/// <summary>
/// Persists attendance on the booking row and writes the operator-action ALLOW in the same
/// transaction. Unknown booking → <see langword="null"/> so the controller can 404 without a
/// domain exception (middleware is not extended for this door).
/// </summary>
public sealed class RecordAttendanceCommandHandler(
    IBookingRepository bookings,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<RecordAttendanceCommand, RecordAttendanceResult?>
{
    public async Task<RecordAttendanceResult?> Handle(
        RecordAttendanceCommand command, CancellationToken ct)
    {
        var status = command.Attendance == "attended"
            ? AttendanceStatus.Attended
            : AttendanceStatus.NoShow;
        var now = DateTimeOffset.UtcNow;
        var rows = 0;

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            rows = await bookings.TrySetAttendanceAsync(
                command.TenantId, command.BookingId, status, now, ct2);
            if (rows == 0)
                return;

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: command.TenantId,
                    ActorType: StaffRole.IsKnown(command.ActorRole) ? command.ActorRole : "operator",
                    ActorId: string.IsNullOrWhiteSpace(command.Actor) ? "unknown" : command.Actor,
                    Action: "booking.attendance_recorded",
                    TargetType: "booking",
                    TargetId: command.BookingId,
                    Decision: "allow",
                    DataClasses: ["operations"],
                    ReasonCode: string.IsNullOrWhiteSpace(command.ReasonCode) ? null : command.ReasonCode,
                    CorrelationId: command.BookingId,
                    OccurredAt: now,
                    Extra: new { attendance = command.Attendance }),
                ct2);
        }, ct);

        return rows == 0
            ? null
            : new RecordAttendanceResult(command.BookingId, command.Attendance);
    }
}
