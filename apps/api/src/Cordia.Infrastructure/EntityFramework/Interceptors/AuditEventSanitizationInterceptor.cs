using Cordia.Domain.Entities;
using Cordia.Domain.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cordia.Infrastructure.EntityFramework.Interceptors;

/// <summary>
/// Spec 14 / Inv-12 uniform audit hygiene: EVERY <see cref="AuditEvent"/> row
/// insert — whether written through <c>IAuditWriter</c>, an ad hoc handler
/// <c>new AuditEvent</c>, or a direct <c>db.AuditEvents.Add</c> — has its
/// metadata (structure-aware) plus actor, subject_ref, and correlation_id
/// (free-text) run through <see cref="SensitiveDataMask"/> before it is
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

            // The free-text identity columns can be CLIENT-supplied at several of the
            // ~45 direct `new AuditEvent` sites, so each masks at this choke point
            // (Inv-12); system-minted values carry no maskable shape and pass
            // byte-identical, and nothing queries or joins on any of these columns:
            // - actor: confirmation.Identity on the submit paths, customerRef via
            //   AuditEnvelope;
            // - subject_ref: CheckVerificationHandlers' grant_issued row stores
            //   challenge.CustomerId raw from the session-scoped POST /api/verifications
            //   body;
            // - correlation_id: often the SAME client-influenced id as subject_ref (a
            //   transcript read audits TargetId == CorrelationId == the route-supplied
            //   session id) — masking one column but not its twin would persist the exact
            //   value just deemed a PAN, one field over. Masked audit rows still
            //   correlate with EACH OTHER (every copy masks to the same token), though a
            //   masked value no longer joins to outbox/inbox rows carrying it raw —
            //   acceptable, since system correlation ids are letter-prefixed and never
            //   mask.
            // Adding a free-text column to AuditEvent? Add it here.
            entry.Entity.Actor = MaskFreeText(entry.Entity.Actor)!;
            entry.Entity.SubjectRef = MaskFreeText(entry.Entity.SubjectRef)!;
            entry.Entity.CorrelationId = MaskFreeText(entry.Entity.CorrelationId);
        }
    }

    private static string? MaskFreeText(string? value)
        => string.IsNullOrEmpty(value) ? value : SensitiveDataMask.Sanitize(value);
}
