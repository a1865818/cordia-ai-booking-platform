using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.EntityFramework;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.Repositories;

/// <inheritdoc/>
public sealed class CustomerOperatorNotesRepository(ApplicationDbContext db) : ICustomerOperatorNotesRepository
{
    public Task<CustomerOperatorNotes?> GetAsync(string tenantId, string customerRef, CancellationToken ct = default)
        => db.CustomerOperatorNotes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.TenantId == tenantId && n.CustomerRef == customerRef, ct);

    public async Task UpsertAsync(CustomerOperatorNotes notes, CancellationToken ct = default)
    {
        var existing = await db.CustomerOperatorNotes
            .FirstOrDefaultAsync(n => n.TenantId == notes.TenantId && n.CustomerRef == notes.CustomerRef, ct);

        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            notes.UpdatedAt = now;
            db.CustomerOperatorNotes.Add(notes);
        }
        else
        {
            existing.Note = notes.Note;
            existing.TagsJson = notes.TagsJson;
            existing.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
    }
}
