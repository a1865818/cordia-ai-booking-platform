using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Services;

/// <summary>
/// The one implementation of <see cref="ICapacityLockOrder"/>. See that interface for the rule and
/// for why it is centralised.
///
/// <para>Must be called inside an ambient transaction (via <c>IUnitOfWork.ExecuteInTransactionAsync</c>
/// or the inbox behaviour). It shares the scoped <c>ApplicationDbContext</c> with its caller and
/// opens no transaction of its own — a lock taken outside a transaction would be released
/// immediately and would order nothing.</para>
/// </summary>
public sealed class CapacityLockOrder(
    ICapacityHoldRepository holds,
    ICapacitySlotRepository slots) : ICapacityLockOrder
{
    /// <inheritdoc/>
    public async Task AcquireAsync(
        string tenantId,
        IEnumerable<string?> holdIds,
        IEnumerable<string?> slotIds,
        CancellationToken ct = default)
    {
        // Holds first (clause 1), each set in ascending id order (clauses 2 and 3).
        foreach (var holdId in Canonical(holdIds))
            await holds.LockAsync(tenantId, holdId, ct);

        foreach (var slotId in Canonical(slotIds))
            await slots.LockAsync(tenantId, slotId, ct);
    }

    /// <summary>
    /// Drops nulls/blanks, collapses duplicates, and sorts ascending.
    ///
    /// <para><b>Ordinal, deliberately.</b> The comparison has to be identical in every process that
    /// competes for these rows; a culture-sensitive comparison is not, so two nodes under different
    /// cultures could order the same pair of ids differently and rebuild the very cycle this type
    /// exists to prevent. Ordinal is a total order and is the same everywhere.</para>
    /// </summary>
    private static IEnumerable<string> Canonical(IEnumerable<string?> ids)
        => ids.Where(id => !string.IsNullOrEmpty(id))
              .Select(id => id!)
              .Distinct(StringComparer.Ordinal)
              .OrderBy(id => id, StringComparer.Ordinal);
}
