using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Cordia.Infrastructure.EntityFramework.Concurrency;

/// <summary>
/// The §22.1 optimistic-concurrency version-token UPDATE: a single atomic
/// <c>ExecuteUpdateAsync</c> statement of the shape
/// <code>
/// UPDATE &lt;table&gt; SET &lt;caller fields&gt;, version = version + 1
/// WHERE &lt;key predicate&gt; AND version = :expectedVersion
/// </code>
/// The version bump is owned by this helper so callers cannot forget it. Returns
/// rows-affected: <c>1</c> = won the race, <c>0</c> = a concurrent worker advanced
/// the row (or the lease was reclaimed) → the caller must reload and re-evaluate
/// rather than force the transition (§6.2, Inv-5). Used by specs 06/07/09.
/// </summary>
public static class VersionTokenUpdate
{
    /// <summary>
    /// <c>true</c> when the conditional UPDATE affected zero rows, meaning the caller
    /// lost the race and must reload the entity (§6.2 lost-race recovery).
    /// </summary>
    public static bool ShouldReload(int rowsAffected) => rowsAffected == 0;

    /// <summary>
    /// Conditional UPDATE that applies the caller-supplied setters AND increments
    /// <c>version</c> in one atomic statement, guarded by <c>version = expectedVersion</c>.
    /// The global query filters are ignored, so the caller's <paramref name="keyPredicate"/>
    /// MUST include the tenant scope (e.g. <c>e =&gt; e.TenantId == t &amp;&amp; e.Id == id</c>).
    /// </summary>
    public static Task<int> UpdateAsync<TEntity>(
        DbContext context,
        long expectedVersion,
        Expression<Func<TEntity, bool>> keyPredicate,
        Action<UpdateSettersBuilder<TEntity>> setters,
        CancellationToken ct = default)
        where TEntity : class
    {
        return context.Set<TEntity>()
            .IgnoreQueryFilters()
            .Where(keyPredicate)
            .Where(e => EF.Property<long>(e, "Version") == expectedVersion)
            .ExecuteUpdateAsync(
                b =>
                {
                    setters(b);
                    b.SetProperty(
                        e => EF.Property<long>(e, "Version"),
                        e => EF.Property<long>(e, "Version") + 1);
                },
                ct);
    }

    /// <summary>
    /// Version-only conditional UPDATE (bumps <c>version</c> with no other field change),
    /// guarded by <c>version = expectedVersion</c>.
    /// </summary>
    public static Task<int> UpdateAsync<TEntity>(
        DbContext context,
        long expectedVersion,
        Expression<Func<TEntity, bool>> keyPredicate,
        CancellationToken ct = default)
        where TEntity : class
        => UpdateAsync(context, expectedVersion, keyPredicate, static _ => { }, ct);
}
