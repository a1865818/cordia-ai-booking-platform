using System.Text.Json;
using System.Text.Json.Nodes;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;

namespace Cordia.Application.Common.Audit;

/// <summary>
/// Builds the ratified spec-12 audit_events row from a typed
/// <see cref="AuditRecord"/> and appends it via the insert-only repository
/// (append-only, Inv-14). The spec-14 envelope fields with no dedicated
/// column (actor_type, target_type, decision, reason_code, data_classes,
/// causation_id) ride in the sanitized metadata jsonb (D13) — no DDL change.
/// </summary>
public sealed class AuditWriter(IAuditEventRepository auditRepo) : IAuditWriter
{
    public async Task WriteAsync(AuditRecord record, CancellationToken ct = default)
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
                    metadata[key] = value;
                }
            }
        }

        await auditRepo.AddAsync(
            new AuditEvent
            {
                TenantId = record.TenantId,
                EventId = $"evt_{Guid.NewGuid():N}",
                EventType = record.Action,
                Actor = record.ActorId,
                SubjectRef = record.TargetId,
                CorrelationId = record.CorrelationId,
                OperationId = record.OperationId,
                Metadata = SensitiveDataMask.Sanitize(metadata.ToJsonString()),
                OccurredAt = DateTimeOffset.UtcNow,
            },
            ct);
    }
}
