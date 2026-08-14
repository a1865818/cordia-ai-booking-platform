using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <inheritdoc/>
public sealed class AgentKnowledgeRepository(ApplicationDbContext db) : IAgentKnowledgeRepository
{
    public async Task<IReadOnlyList<AgentKnowledgeEntry>> ListAsync(string tenantId, CancellationToken ct = default)
        => await db.AgentKnowledgeEntries
            .AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(ct);

    public Task<AgentKnowledgeEntry?> GetAsync(string tenantId, string entryId, CancellationToken ct = default)
        => db.AgentKnowledgeEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EntryId == entryId, ct);

    public async Task AddAsync(AgentKnowledgeEntry entry, CancellationToken ct = default)
    {
        db.AgentKnowledgeEntries.Add(entry);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(AgentKnowledgeEntry entry, CancellationToken ct = default)
    {
        var existing = await db.AgentKnowledgeEntries
            .FirstOrDefaultAsync(e => e.TenantId == entry.TenantId && e.EntryId == entry.EntryId, ct);
        if (existing is null)
            return;

        existing.Question = entry.Question;
        existing.Answer = entry.Answer;
        existing.Status = entry.Status;
        existing.Source = entry.Source;
        existing.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
