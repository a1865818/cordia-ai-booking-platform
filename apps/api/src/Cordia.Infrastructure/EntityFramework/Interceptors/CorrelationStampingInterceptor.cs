using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cordia.Infrastructure.EntityFramework.Interceptors;

/// <summary>
/// EF Core SaveChanges interceptor that auto-stamps <see cref="ICorrelatable"/> entities with the
/// ambient <see cref="AmbientCorrelationContext"/> on insert (spec 13 §4.1, §25.1).
///
/// <para>Fail-open: any exception during stamping is swallowed so the business path is never
/// disrupted by an absent or malformed ambient correlation (spec 13 §8).</para>
/// </summary>
public sealed class CorrelationStampingInterceptor : SaveChangesInterceptor
{
    private readonly ICorrelationContext _correlation;

    public CorrelationStampingInterceptor(ICorrelationContext correlation)
    {
        _correlation = correlation;
    }

    /// <inheritdoc/>
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        StampEntries(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc/>
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        StampEntries(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void StampEntries(DbContext? context)
    {
        if (context is null) return;
        try
        {
            var ctx = _correlation.Current;
            var added = context.ChangeTracker
                .Entries<ICorrelatable>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity);

            Stamp(added, ctx);
        }
        catch
        {
            // Fail-open: swallow any stamping error — the insert must still succeed.
        }
    }

    /// <summary>
    /// Unit-testable static stamping helper: fills null/empty correlation fields on each added
    /// entity from <paramref name="ctx"/>. Never overwrites a value already set by a handler.
    /// </summary>
    /// <param name="added">Entities in the Added state that implement <see cref="ICorrelatable"/>.</param>
    /// <param name="ctx">The ambient correlation context to stamp from.</param>
    internal static void Stamp(IEnumerable<ICorrelatable> added, AmbientCorrelationContext ctx)
    {
        foreach (var entity in added)
        {
            if (string.IsNullOrEmpty(entity.CorrelationId) && !string.IsNullOrEmpty(ctx.CorrelationId))
                entity.CorrelationId = ctx.CorrelationId;

            if (string.IsNullOrEmpty(entity.CausationId) && !string.IsNullOrEmpty(ctx.CausationId))
                entity.CausationId = ctx.CausationId;
        }
    }
}
