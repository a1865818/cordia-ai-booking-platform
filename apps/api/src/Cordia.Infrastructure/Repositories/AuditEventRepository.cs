using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>
/// Insert-only EF Core implementation of <see cref="IAuditEventRepository"/>.
/// No update or delete methods exist — audit_events is append-only (Inv-14).
/// </summary>
public class AuditEventRepository(ApplicationDbContext db) : IAuditEventRepository
{
    public async Task AddAsync(AuditEvent auditEvent, CancellationToken ct = default)
    {
        db.AuditEvents.Add(auditEvent);
        await db.SaveChangesAsync(ct);
    }

    public Task<AuditEvent?> GetAsync(string eventId, CancellationToken ct = default)
        => db.AuditEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == eventId, ct);
}
