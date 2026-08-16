using System.Text.Json;
using System.Text.Json.Nodes;
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
                // Control characters are dropped FIRST (see StripControlCharacters) — a masked row is
                // no use if the INSERT itself cannot execute.
                entry.Entity.Metadata = SensitiveDataMask.SanitizeJson(StripControlCharacters(metadata));

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
        => string.IsNullOrEmpty(value) ? value : RemoveControlCharacters(SensitiveDataMask.Sanitize(value));

    /// <summary>
    /// Drops control characters from every string VALUE in the metadata JSON — a write-availability
    /// guard that belongs at the same choke point as the masking, for the same reason: it must hold
    /// for EVERY write site, including ones not yet written.
    ///
    /// <para><b>PostgreSQL cannot store <c>U+0000</c>.</b> A <c>jsonb</c> column rejects the escape
    /// (<c>22P05 unsupported Unicode escape sequence</c>) and a <c>text</c> column rejects the raw
    /// byte. Until now no audit metadata carried anything but code-authored literals, so this could
    /// not happen; the menu-extraction lane is the first to copy model-generated free text — shaped by
    /// an untrusted uploaded document — into <c>metadata</c>, and <c>JsonSerializer</c> encodes an
    /// embedded NUL as a perfectly valid JSON escape. The resulting failure lands in
    /// <c>SaveChanges</c>, i.e. AFTER every handler's own error handling, so it rolls back whatever
    /// transaction is open — for an inbox consumer that un-admits the message and it redelivers
    /// forever (Inv-5). All control characters go, not just NUL: none of them have a legitimate
    /// meaning in an operator-facing audit value, and NUL is merely the one the database happens to
    /// reject today.</para>
    ///
    /// <para>Structural, not a string replace: naively deleting the escape text would corrupt a
    /// document whose data legitimately contains a backslash followed by <c>u0000</c>, turning a
    /// storable row into an unparseable one — the very failure this prevents. Object KEYS are not
    /// walked; every metadata object in the codebase is serialized from an anonymous type, so its
    /// keys are compile-time literals.</para>
    /// </summary>
    private static string StripControlCharacters(string metadata)
    {
        // Fast path: a control character reaches here either as a serializer-emitted \u00XX escape
        // (System.Text.Json escapes all of them) or, for hand-assembled metadata, as a raw char.
        if (!metadata.Contains("\\u00", StringComparison.OrdinalIgnoreCase) && !metadata.Any(char.IsControl))
            return metadata;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(metadata);
        }
        catch (JsonException)
        {
            // Not JSON: SanitizeJson will mask it wholesale INTO a JSON string, so raw control
            // characters still have to go first.
            return RemoveControlCharacters(metadata);
        }

        var changed = false;
        var stripped = StripNode(root, ref changed);
        return changed ? (stripped?.ToJsonString() ?? metadata) : metadata;
    }

    private static JsonNode? StripNode(JsonNode? node, ref bool changed)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    var replacement = StripNode(value, ref changed);
                    if (!ReferenceEquals(replacement, value))
                        obj[key] = replacement;
                }
                return node;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var value = arr[i];
                    var replacement = StripNode(value, ref changed);
                    if (!ReferenceEquals(replacement, value))
                        arr[i] = replacement;
                }
                return node;
            case JsonValue scalar when scalar.TryGetValue<string>(out var text) && text.Any(char.IsControl):
                changed = true;
                return JsonValue.Create(RemoveControlCharacters(text));
            default:
                return node;
        }
    }

    private static string RemoveControlCharacters(string value)
        => value.Any(char.IsControl)
            ? new string(value.Where(c => !char.IsControl(c)).ToArray())
            : value;
}
