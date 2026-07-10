using Cordia.Domain.Entities;
using Cordia.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cordia.Infrastructure.EntityFramework.Interceptors;

/// <summary>
/// Spec 14 / Inv-12 uniform audit hygiene: EVERY <see cref="AuditEvent"/> row
/// insert — whether written through <c>IAuditWriter</c>, an ad hoc handler
/// <c>new AuditEvent</c>, or a direct <c>db.AuditEvents.Add</c> — has its
/// metadata run through <see cref="SensitiveDataMask.Sanitize"/> before it is
/// persisted. This is the single choke point that makes the 35+ existing
/// write sites (and any future one) uniformly safe without per-site churn;
/// <c>IAuditWriter</c> remains the richer envelope for security-DECISION
/// events, while workflow-progress events stay direct-but-sanitized.
/// Stateless and side-effect-free; registered once in
/// <c>ApplicationDbContext.OnConfiguring</c> so every context instance
/// (production DI, test fixtures, design-time) gets it automatically.
/// </summary>
public sealed class AuditEventSanitizationInterceptor : SaveChangesInterceptor
{
    public static readonly AuditEventSanitizationInterceptor Instance = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        SanitizeAddedAuditEvents(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        SanitizeAddedAuditEvents(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void SanitizeAddedAuditEvents(DbContext? context)
    {
        if (context is null)
            return;

        foreach (var entry in context.ChangeTracker.Entries<AuditEvent>())
        {
            if (entry.State != EntityState.Added)
                continue;

            var metadata = entry.Entity.Metadata;
            if (!string.IsNullOrEmpty(metadata))
                // Structure-aware (SanitizeJson, not free-text Sanitize): the metadata column holds a
                // JSON object, so this keeps the control-plane id exemption (target_id/*_id and the
                // allowlisted system-minted refs — user-influenced keys like customer_ref are NOT exempt)
                // intact while masking PANs in free-text fields. Free-text Sanitize would corrupt a
                // Luhn-coincidental id to [REDACTED]. Non-JSON metadata falls back to a wholesale mask.
                entry.Entity.Metadata = SensitiveDataMask.SanitizeJson(metadata);
        }
    }
}
