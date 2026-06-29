using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Notifications.MissingTerminalAudit;

/// <summary>
/// Application-layer implementation of <see cref="IMissingNotificationAuditor"/> that delegates
/// to <see cref="AuditMissingTerminalNotificationCommand"/> via MediatR.
///
/// Registered as Scoped in <c>AddApplication()</c>; resolved by
/// <c>MissingNotificationAuditHostedService</c> through the domain interface so Infrastructure
/// does not reference Application directly.
/// </summary>
public sealed class MediatRMissingNotificationAuditor(IMediator mediator) : IMissingNotificationAuditor
{
    public async Task AuditAsync(string tenantId, string operationId, OperationStatus terminalStatus, CancellationToken ct = default)
        => await mediator.Send(new AuditMissingTerminalNotificationCommand(tenantId, operationId, terminalStatus), ct);
}
