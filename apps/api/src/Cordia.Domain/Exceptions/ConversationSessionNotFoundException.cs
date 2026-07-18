namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown when a consent update targets a conversation session that does not
/// exist for the tenant (spec 14 §5.1; the gateway is the single row creator,
/// D12 — consent recording never creates the row). Maps to HTTP 404.
/// </summary>
public sealed class ConversationSessionNotFoundException(string tenantId, string sessionId)
    : DomainException($"Conversation session '{sessionId}' was not found for tenant '{tenantId}'.")
{
    public string TenantId { get; } = tenantId;

    public string SessionId { get; } = sessionId;
}
