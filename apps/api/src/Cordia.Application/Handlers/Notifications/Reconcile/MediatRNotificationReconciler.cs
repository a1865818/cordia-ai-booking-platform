using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Notifications.Reconcile;

/// <summary>
/// Application-layer implementation of <see cref="INotificationReconciler"/> that delegates
/// to <see cref="ReconcileNotificationCommand"/> via MediatR.
///
/// Registered as Scoped in <c>AddApplication()</c>; resolved by
/// <c>ReconcileNotificationsHostedService</c> through the domain interface so Infrastructure
/// does not reference Application directly.
/// </summary>
public sealed class MediatRNotificationReconciler(IMediator mediator) : INotificationReconciler
{
    public Task ReconcileAsync(string tenantId, string notificationId, CancellationToken ct = default)
        => mediator.Send(new ReconcileNotificationCommand(tenantId, notificationId), ct);
}
