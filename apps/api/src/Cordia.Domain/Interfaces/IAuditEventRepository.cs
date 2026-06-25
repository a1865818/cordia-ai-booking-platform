using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>
/// Insert-only repository for <see cref="AuditEvent"/> rows (append-only, Inv-14).
/// No update or delete methods exist by design.
/// </summary>
public interface IAuditEventRepository
{
    Task AddAsync(AuditEvent auditEvent, CancellationToken ct = default);

    /// <summary>Read-only fetch by event ID (AsNoTracking). Used for round-trip tests.</summary>
    Task<AuditEvent?> GetAsync(string eventId, CancellationToken ct = default);
}
