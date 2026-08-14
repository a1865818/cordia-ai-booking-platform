using Cordia.Domain.Entities;

namespace Cordia.Domain.Interfaces;

/// <summary>Operator-written notes and tags for a customer.</summary>
public interface ICustomerOperatorNotesRepository
{
    Task<CustomerOperatorNotes?> GetAsync(string tenantId, string customerRef, CancellationToken ct = default);

    Task UpsertAsync(CustomerOperatorNotes notes, CancellationToken ct = default);
}
