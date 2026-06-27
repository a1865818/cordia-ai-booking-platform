using System.Text.Json;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.BookingFulfillment.Cancellation;

/// <summary>
/// Sets the <c>CancellationRequestedAt</c> flag on a non-terminal booking operation (spec 06
/// §15.5, FM#13). The flag is observed at the next workflow checkpoint; it does NOT perform a
/// synchronous status transition.
///
/// Note: Post-booking cancellation (an already-Booked operation) is out of scope for T8.
/// That will be implemented as a separate new operation in spec 08.
/// </summary>
public sealed class RequestCancellationCommandHandler(
    IBookingOperationRepository opRepo,
    IAuditEventRepository auditRepo,
    IUnitOfWork uow)
    : IRequestHandler<RequestCancellationCommand, bool>
{
    public async Task<bool> Handle(RequestCancellationCommand command, CancellationToken ct)
    {
        var op = await opRepo.GetAsync(command.OperationId, ct);

        if (op is null || op.TenantId != command.TenantId)
            return false;

        var rows = await opRepo.SetCancellationRequestedAsync(
            command.TenantId, command.OperationId, DateTimeOffset.UtcNow, ct);

        if (rows > 0)
        {
            await uow.ExecuteInTransactionAsync(async ct2 =>
            {
                var now = DateTimeOffset.UtcNow;
                await auditRepo.AddAsync(new AuditEvent
                {
                    TenantId = command.TenantId,
                    EventId = $"evt_{Guid.NewGuid():N}",
                    EventType = "operation.cancellation_requested",
                    Actor = "user",
                    SubjectRef = command.OperationId,
                    CorrelationId = command.OperationId,
                    OperationId = command.OperationId,
                    Metadata = JsonSerializer.Serialize(new { operation_id = command.OperationId }),
                    OccurredAt = now,
                }, ct2);
            }, ct);
        }

        return rows > 0;
    }
}
