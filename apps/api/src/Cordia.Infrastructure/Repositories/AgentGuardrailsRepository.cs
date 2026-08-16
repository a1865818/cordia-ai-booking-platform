using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <inheritdoc/>
public sealed class AgentGuardrailsRepository(ApplicationDbContext db) : IAgentGuardrailsRepository
{
    public Task<AgentGuardrails?> GetAsync(string tenantId, CancellationToken ct = default)
        => db.AgentGuardrails
            .AsNoTracking()
            .FirstOrDefaultAsync(g => g.TenantId == tenantId, ct);

    public async Task UpsertAsync(AgentGuardrails guardrails, CancellationToken ct = default)
    {
        var existing = await db.AgentGuardrails
            .FirstOrDefaultAsync(g => g.TenantId == guardrails.TenantId, ct);

        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            guardrails.UpdatedAt = now;
            db.AgentGuardrails.Add(guardrails);
        }
        else
        {
            existing.TogglesJson = guardrails.TogglesJson;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
