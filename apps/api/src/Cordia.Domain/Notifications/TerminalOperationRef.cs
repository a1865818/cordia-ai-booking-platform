namespace Cordia.Domain.Notifications;

/// <summary>
/// Lightweight projection returned by
/// <see cref="Interfaces.INotificationRepository.ListTerminalOperationsMissingAcceptedNotificationAsync"/>
/// — carries only the tenant + operation identifiers needed to dispatch the per-op audit.
/// </summary>
public sealed record TerminalOperationRef(string TenantId, string OperationId);
