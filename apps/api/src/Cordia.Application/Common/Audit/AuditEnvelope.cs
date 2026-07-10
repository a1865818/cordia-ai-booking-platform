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
        var metadata = new JsonObject
        {
            ["actor_type"] = record.ActorType,
            ["action"] = record.Action,
            ["target_type"] = record.TargetType,
            ["target_id"] = record.TargetId,
            ["decision"] = record.Decision,
            ["data_classes"] = new JsonArray(
                record.DataClasses.Select(dc => (JsonNode?)dc).ToArray()),
        };
        if (record.ReasonCode is not null)
            metadata["reason_code"] = record.ReasonCode;
        if (record.CausationId is not null)
            metadata["causation_id"] = record.CausationId;

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
            SubjectRef = record.TargetId,
            CorrelationId = record.CorrelationId,
            OperationId = record.OperationId,
            // Structure-aware sanitize (not free-text Sanitize): the metadata IS a JSON object, so
            // SanitizeJson preserves the control-plane id exemption (target_id/operation_id/grant_id/
            // and the allowlisted system-minted refs) while still masking a PAN in any free-text
            // field — user-influenced keys like customer_ref are NOT exempt. Free-text Sanitize would
            // corrupt a Luhn-coincidental numeric/string target_id to [REDACTED], breaking audit joins.
            // Defense-in-depth: the DbContext-level AuditEventSanitizationInterceptor re-sanitizes
            // every row at save time (also via SanitizeJson) regardless of write path.
            Metadata = SensitiveDataMask.SanitizeJson(metadata.ToJsonString()),
            OccurredAt = record.OccurredAt ?? DateTimeOffset.UtcNow,
        };
    }
}
