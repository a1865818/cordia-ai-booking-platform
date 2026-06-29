using Cordia.Domain.Enums;

namespace Cordia.Domain.Notifications;

/// <summary>
/// Lightweight projection returned by
/// <see cref="Interfaces.INotificationRepository.ListTerminalOperationsMissingAcceptedNotificationAsync"/>
/// — carries the tenant + operation identifiers and the terminal status needed to dispatch the per-op audit.
/// </summary>
public sealed record TerminalOperationRef(string TenantId, string OperationId, OperationStatus Status);
