using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingOperations.GetOperationStatus;

/// <summary>Query to look up the current status of a booking operation (spec 04 §3).</summary>
public record GetOperationStatusQuery(string TenantId, string OperationId) : IRequest<OperationStatusView?>;

/// <summary>
/// Returns the <see cref="OperationStatusView"/> for the requested operation, or <c>null</c>
/// when no operation exists under the tenant (→ 404 in the controller).
/// </summary>
public sealed class GetOperationStatusQueryHandler(IBookingOperationRepository operationRepo)
    : IRequestHandler<GetOperationStatusQuery, OperationStatusView?>
{
    public async Task<OperationStatusView?> Handle(GetOperationStatusQuery request, CancellationToken ct)
    {
        var op = await operationRepo.GetAsync(request.OperationId, ct);
        if (op is null || op.TenantId != request.TenantId)
            return null;

        // Map internal OperationStatus to NormalizedOperationStatusEnum (9-value public projection,
        // Inv-16 — internal substates are never surfaced directly).
        var normalized = op.Status switch
        {
            Domain.Enums.OperationStatus.Accepted => NormalizedOperationStatusEnum.Accepted,
            Domain.Enums.OperationStatus.Validating => NormalizedOperationStatusEnum.InProgress,
            Domain.Enums.OperationStatus.AwaitingUserAction => NormalizedOperationStatusEnum.AwaitingUserAction,
            Domain.Enums.OperationStatus.ReservingCapacity => NormalizedOperationStatusEnum.InProgress,
            Domain.Enums.OperationStatus.Held => NormalizedOperationStatusEnum.InProgress,
            Domain.Enums.OperationStatus.AwaitingPayment => NormalizedOperationStatusEnum.InProgress,
            Domain.Enums.OperationStatus.Finalizing => NormalizedOperationStatusEnum.InProgress,
            Domain.Enums.OperationStatus.Booked => NormalizedOperationStatusEnum.Booked,
            Domain.Enums.OperationStatus.AlternativesRequired => NormalizedOperationStatusEnum.AlternativesRequired,
            Domain.Enums.OperationStatus.Reconciling => NormalizedOperationStatusEnum.InProgress,
            Domain.Enums.OperationStatus.CompensationRequired => NormalizedOperationStatusEnum.InProgress,
            Domain.Enums.OperationStatus.ManualReview => NormalizedOperationStatusEnum.InProgress,
            Domain.Enums.OperationStatus.Rejected => NormalizedOperationStatusEnum.Rejected,
            Domain.Enums.OperationStatus.Expired => NormalizedOperationStatusEnum.Expired,
            Domain.Enums.OperationStatus.Waitlisted => NormalizedOperationStatusEnum.Waitlisted,
            Domain.Enums.OperationStatus.FailedFinal => NormalizedOperationStatusEnum.Failed,
            _ => throw new ArgumentOutOfRangeException(nameof(op.Status), op.Status,
                "Unmapped OperationStatus — add it to the normalized-status projection."),
        };

        bool terminal = normalized is
            NormalizedOperationStatusEnum.Booked or
            NormalizedOperationStatusEnum.Rejected or
            NormalizedOperationStatusEnum.Expired or
            NormalizedOperationStatusEnum.Failed;

        return new OperationStatusView
        {
            OperationId = op.OperationId,
            Status = normalized,
            Pending = !terminal,
            Terminal = terminal,
            BookingId = op.BookingId ?? string.Empty,
            CustomerMessage = CustomerMessageFor(normalized),
            UpdatedAt = op.UpdatedAt,
        };
    }

    private static string CustomerMessageFor(NormalizedOperationStatusEnum status) => status switch
    {
        NormalizedOperationStatusEnum.Accepted => "Your request has been received and is being processed.",
        NormalizedOperationStatusEnum.InProgress => "Your request is being processed.",
        NormalizedOperationStatusEnum.AwaitingUserAction => "We need a little more information to continue.",
        NormalizedOperationStatusEnum.AlternativesRequired => "The requested option isn't available; please choose an alternative.",
        NormalizedOperationStatusEnum.Booked => "Your booking is confirmed.",
        NormalizedOperationStatusEnum.Waitlisted => "You've been added to the waitlist.",
        NormalizedOperationStatusEnum.Rejected => "Your request could not be completed.",
        NormalizedOperationStatusEnum.Expired => "This request has expired.",
        NormalizedOperationStatusEnum.Failed => "Your request could not be completed.",
        _ => "Your request is being processed.",
    };
}
