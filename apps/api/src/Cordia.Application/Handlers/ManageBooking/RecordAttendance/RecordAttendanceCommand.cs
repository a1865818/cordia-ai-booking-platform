using System.Text.Json.Serialization;
using Cordia.Application.Common.Authorization;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.ManageBooking.RecordAttendance;

/// <summary>
/// Records whether the guest attended. Operator-gated (owner satisfies). Does not change
/// booking lifecycle status — attendance is a separate fact on the booking row.
/// </summary>
public sealed record RecordAttendanceCommand(
    string TenantId,
    string BookingId,
    string Attendance,
    string Actor,
    string ActorRole,
    string ReasonCode) : IRequest<RecordAttendanceResult?>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Operator;

    public string ActionName => "booking.attendance_recorded";

    public string TargetType => "booking";

    public string TargetId => BookingId;
}

public sealed record RecordAttendanceResult(
    [property: JsonPropertyName("booking_id")] string BookingId,
    [property: JsonPropertyName("attendance")] string Attendance);

public sealed class RecordAttendanceValidator : AbstractValidator<RecordAttendanceCommand>
{
    public static readonly string[] Allowed = ["attended", "no_show"];

    public RecordAttendanceValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.BookingId).NotEmpty();
        // Deliberately NO rule on Actor/ActorRole/ReasonCode — missing actor is authorization
        // (OperatorActionBehavior), not shape. A NotEmpty rule here would 400 before the deny
        // audit (spec 14 §3.1 step 5).
        RuleFor(x => x.Attendance)
            .Must(a => Allowed.Contains(a, StringComparer.Ordinal))
            .WithMessage("attendance must be 'attended' or 'no_show'.");
    }
}
