using System.Text.Json;
using System.Text.Json.Nodes;
using Cordia.Domain.Entities;
using Cordia.Domain.Security;

namespace Cordia.Application.Common.Audit;

/// <summary>
/// Builds the ratified spec-12 audit_events row from a typed
/// <see cref="AuditRecord"/> (spec-14 envelope fields packed into sanitized
/// metadata jsonb, D13). Static so both <see cref="AuditWriter"/> and any
/// handler-local audit helper can produce byte-identical envelope rows.
/// </summary>
public static class AuditEnvelope
{
    public static AuditEvent Build(AuditRecord record)
    {
        // Free-text sanitize the CLIENT-influenceable identifiers ONCE, before they fan
        // out into both the metadata jsonb (where "target_id" is a control-plane-EXEMPT
        // key in SanitizeJson, so a raw PAN under it would survive every later sweep)
        // and the subject_ref/correlation_id columns — keeping all copies in one row
        // consistent. System-minted ids (op_/sess-/evt_ prefixed) carry no maskable
        // shape and pass byte-identical; a PAN-shaped route/body value (a session id is
        // a client-supplied route param) masks everywhere at once (Inv-12).
        var targetId = SensitiveDataMask.Sanitize(record.TargetId);
        var correlationId = record.CorrelationId is { Length: > 0 } corr
            ? SensitiveDataMask.Sanitize(corr)
            : record.CorrelationId;

        var metadata = new JsonObject
        {
            ["actor_type"] = record.ActorType,
            ["action"] = record.Action,
            ["target_type"] = record.TargetType,
            ["target_id"] = targetId,
            ["decision"] = record.Decision,
            ["data_classes"] = new JsonArray(
                record.DataClasses.Select(dc => (JsonNode?)dc).ToArray()),
        };
        if (record.ReasonCode is not null)
            metadata["reason_code"] = record.ReasonCode;
        if (record.CausationId is not null)
            // Same reasoning as target_id above: "causation_id" is a control-plane-exempt
            // key in SanitizeJson, so a maskable value must be sanitized BEFORE packing
            // (today every writer passes a system-minted replay/message id — this is the
            // fail-closed parallel of the target_id fix, not a live leak).
            metadata["causation_id"] = SensitiveDataMask.Sanitize(record.CausationId);

        if (record.Extra is not null)
        {
            var extra = JsonSerializer.SerializeToNode(record.Extra) as JsonObject;
            if (extra is not null)
            {
                foreach (var (key, value) in extra.ToList())
                {
                    extra.Remove(key);
                    // Canonical envelope keys win — Extra can never overwrite
                    // the authoritative decision/reason_code/etc.
                    if (!metadata.ContainsKey(key))
                        metadata[key] = value;
                }
            }
        }

        return new AuditEvent
        {
            TenantId = record.TenantId,
            EventId = $"evt_{Guid.NewGuid():N}",
            EventType = record.Action,
            // Free-text sanitize: ActorId can be CLIENT-supplied (customerRef on grant
            // denials, session.CustomerRef on transcript reads), so a PAN-shaped value
            // must not land raw in the top-level actor column (Inv-12). System actors
            // ("system"/"ops.jane"/"agent") carry no maskable shape and pass byte-identical;
            // the rare Luhn-coincidental 13+ digit customer id masks fail-closed — nothing
            // queries or joins on actor, so over-masking costs only that row's attribution.
            // Defense-in-depth, mirroring Metadata: AuditEventSanitizationInterceptor
            // re-sanitizes actor at save time — keep BOTH; the interceptor alone covers the
            // ~45 direct `new AuditEvent` sites that never pass through this envelope.
            Actor = SensitiveDataMask.Sanitize(record.ActorId),
            // Sanitized once above so subject_ref, correlation_id, AND metadata.target_id
            // stay consistent — masking only the column would leave the same PAN raw one
            // field over (metadata's target_id key is control-plane-exempt by design).
            // Defense-in-depth, mirroring Actor: the interceptor re-masks these columns at
            // save time — keep BOTH; the interceptor alone covers the ~45 direct
            // `new AuditEvent` sites that never pass through this envelope.
            SubjectRef = targetId,
            CorrelationId = correlationId,
            OperationId = record.OperationId,
            // Structure-aware sanitize (not free-text Sanitize): the metadata IS a JSON object, so
            // SanitizeJson preserves the control-plane id exemption (operation_id/grant_id/ and the
            // allowlisted system-minted refs — typically Extra-supplied system ids) while still
            // masking a PAN in any free-text field; user-influenced keys like customer_ref are NOT
            // exempt. The exempt target_id/causation_id slots are safe here because their VALUES
            // were free-text sanitized above before packing — the exemption preserves system-minted
            // ids byte-for-byte, and a client-influenceable value never reaches an exempt key raw.
            // Defense-in-depth: the DbContext-level AuditEventSanitizationInterceptor re-sanitizes
            // every row at save time (also via SanitizeJson) regardless of write path.
            Metadata = SensitiveDataMask.SanitizeJson(metadata.ToJsonString()),
            OccurredAt = record.OccurredAt ?? DateTimeOffset.UtcNow,
        };
    }
}
