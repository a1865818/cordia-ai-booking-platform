using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IWorkflowAttemptRepository"/> (append-only, Inv-15).</summary>
public class WorkflowAttemptRepository(ApplicationDbContext db) : IWorkflowAttemptRepository
{
    public async Task AddAsync(WorkflowAttempt attempt, CancellationToken ct = default)
    {
        db.WorkflowAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);
    }

    public Task<WorkflowAttempt?> GetAsync(string attemptId, CancellationToken ct = default)
        => db.WorkflowAttempts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AttemptId == attemptId, ct);
}
