using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Access to issued console operator sessions.</summary>
public interface IOperatorSessionRepository
{
    Task AddAsync(OperatorSession session, CancellationToken ct = default);

    Task<OperatorSession?> GetAsync(string tenantId, string sessionId, CancellationToken ct = default);

    /// <summary>Stamps <c>RevokedAt</c> when the session is still live. No-op if already revoked or missing.</summary>
    Task RevokeAsync(string tenantId, string sessionId, CancellationToken ct = default);
}
