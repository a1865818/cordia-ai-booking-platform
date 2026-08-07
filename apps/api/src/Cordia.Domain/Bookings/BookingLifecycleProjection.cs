using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Generated;

namespace Cordia.Domain.Bookings;

/// <summary>
/// Projects an operation's <see cref="OperationStatus"/> and type onto the lifecycle label a
/// customer-facing reader shows for the BOOKING.
///
/// <para><b>Why this exists as one function.</b> This mapping was written out twice — identically,
/// by hand — in <c>ConsoleReadModel</c> and <c>BookingReadRepository</c>, with a comment on one copy
/// saying it "mirrors" the other "so the console and the manage-booking projection never disagree".
/// Nothing enforced that. Two compiled copies of a switch with catch-all default arms is precisely
/// the shape where a new status changes one reader's answer and not the other's, and the disagreement
/// surfaces as an operator seeing "confirmed" on a booking the guest was told was cancelled.</para>
///
/// <para>In Domain rather than in either reader, because it is a statement about what an operation
/// status MEANS for a booking — the same category of fact as
/// <see cref="OperationStateMachine"/>'s classifications, and owned by spec 06.</para>
/// </summary>
public static class BookingLifecycleProjection
{
    /// <summary>Confirmed and unchanged, as far as this operation is concerned.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>The booking is gone — cancelled, rejected, expired, or finally failed.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>A cancellation is in flight but has not landed.</summary>
    public const string CancellationPending = "cancellation_pending";

    /// <summary>An amendment is in flight, or has landed and the revision carries the change.</summary>
    public const string ModificationPending = "modification_pending";

    /// <summary>
    /// The lifecycle label for a booking, given the operation acting on it.
    /// </summary>
    public static string From(OperationTypeEnum operationType, OperationStatus status)
        => status switch
        {
            // A cancel has no `cancelled` operation status of its own (D1): it terminates at
            // `Booked` — "this operation succeeded" — with the BOOKING carrying the
            // cancellation. This arm MUST precede the generic `Booked` one, or a successfully
            // cancelled booking reads back as "confirmed".
            OperationStatus.Booked when operationType is OperationTypeEnum.Cancel => Cancelled,
            OperationStatus.Booked when operationType is OperationTypeEnum.Modify or OperationTypeEnum.Reschedule
                => ModificationPending,
            OperationStatus.Booked => Confirmed,

            // Asked of the state machine rather than listed as `Rejected or Expired or FailedFinal`.
            // A new terminal failure status now reads as `cancelled` in BOTH readers automatically,
            // instead of falling through the default arms below and reporting `confirmed` for a
            // booking that failed.
            _ when OperationStateMachine.IsTerminalFailure(status) => Cancelled,

            _ when operationType is OperationTypeEnum.Cancel => CancellationPending,
            _ when operationType is OperationTypeEnum.Modify or OperationTypeEnum.Reschedule
                => ModificationPending,
            _ => Confirmed,
        };
}
