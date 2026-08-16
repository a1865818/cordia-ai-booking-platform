using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <inheritdoc/>
public sealed class OperatorSessionRepository(ApplicationDbContext db) : IOperatorSessionRepository
{
    public async Task AddAsync(OperatorSession session, CancellationToken ct = default)
    {
        db.OperatorSessions.Add(session);
        await db.SaveChangesAsync(ct);
    }

    public Task<OperatorSession?> GetAsync(string tenantId, string sessionId, CancellationToken ct = default)
        => db.OperatorSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId && s.SessionId == sessionId, ct);

    public async Task RevokeAsync(string tenantId, string sessionId, CancellationToken ct = default)
    {
        await db.OperatorSessions
            .Where(s => s.TenantId == tenantId && s.SessionId == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct);
    }
}
