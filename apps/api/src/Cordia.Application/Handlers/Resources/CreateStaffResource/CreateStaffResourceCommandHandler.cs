using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Resources.ListStaffResources;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Resources.CreateStaffResource;

/// <summary>
/// Inserts an active staff resource and writes the owner-action ALLOW in the same transaction.
/// </summary>
public sealed class CreateStaffResourceCommandHandler(
    IStaffResourceRepository resources,
    IAuditWriter audit,
    IUnitOfWork uow)
    : IRequestHandler<CreateStaffResourceCommand, StaffResourceView>
{
    public async Task<StaffResourceView> Handle(
        CreateStaffResourceCommand command, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var trimmedName = command.Name.Trim();
        var role = string.IsNullOrWhiteSpace(command.Role) ? null : command.Role.Trim();
        var resourceId = $"staff_{Guid.NewGuid():N}";

        var resource = new StaffResource
        {
            TenantId = command.TenantId,
            ResourceId = resourceId,
            Name = trimmedName,
            Role = role,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            await resources.AddAsync(resource, ct2);

            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: command.TenantId,
                    ActorType: StaffRole.IsKnown(command.ActorRole) ? command.ActorRole : "owner",
                    ActorId: string.IsNullOrWhiteSpace(command.Actor) ? "unknown" : command.Actor,
                    Action: "staff_resource.created",
                    TargetType: "staff_resource",
                    TargetId: resourceId,
                    Decision: "allow",
                    DataClasses: ["operations"],
                    ReasonCode: string.IsNullOrWhiteSpace(command.ReasonCode) ? null : command.ReasonCode,
                    CorrelationId: resourceId,
                    OccurredAt: now),
                ct2);
        }, ct);

        return new StaffResourceView(resourceId, trimmedName, role, Active: true);
    }
}
