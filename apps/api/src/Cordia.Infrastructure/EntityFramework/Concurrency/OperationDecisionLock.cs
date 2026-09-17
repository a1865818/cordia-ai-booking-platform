using Cordia.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cordia.Infrastructure.EntityFramework.Concurrency;

/// <summary>
/// <see cref="IOperationDecisionLock"/> as a PostgreSQL transaction-scoped advisory lock (#108, D3).
///
/// <para><b>Why a lock rather than a version bump.</b> Bumping <c>booking_operations.version</c> from
/// the dispatch fence would make the cancel CAS lose — and every other version-guarded transition
/// holding a pre-fence version too. There are 49 such call sites, and this codebase reads a 0-row
/// result as "re-drive, not an error", so collisions nobody intended would be silent. The lock is
/// taken at exactly the two decision points and touches no schema.</para>
///
/// <para><b>The key.</b> The two-integer form, namespaced by <see cref="LockClass"/> so it can never
/// meet a single-bigint advisory lock (the integration suite's schema lock is one). The object id is
/// <c>hashtext</c> of tenant and operation; a hash collision only serialises two unrelated
/// operations briefly, which is safe.</para>
/// </summary>
public sealed class OperationDecisionLock(ApplicationDbContext db) : IOperationDecisionLock
{
    /// <summary>Advisory-lock namespace for operation decisions. Arbitrary, fixed, non-zero.</summary>
    internal const int LockClass = 108_051;

    public async Task AcquireAsync(string tenantId, string operationId, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException(
                "IOperationDecisionLock must be acquired inside a transaction; outside one the advisory "
                + "lock is released as soon as the statement completes and serialises nothing.");

        var key = $"{tenantId}:{operationId}";
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({LockClass}, hashtext({key}))", ct);
    }
}
