using Cordia.Domain.Interfaces;

namespace Cordia.Application.Common.Audit;

/// <summary>
/// The spec-14 audit write path (D13) for security-DECISION events —
/// consent, grant issuance/denial, sensitive reads, operator/owner actions —
/// where the envelope (actor_type, reason_code, decision, data_classes)
/// matters. Delegates row construction to <see cref="AuditEnvelope.Build"/>
/// and appends via the insert-only repository (append-only, Inv-14).
///
/// Two-population norm (gate decision 2026-07-10): workflow-PROGRESS events
/// (system-actor state-transition trail, e.g. operation.booked,
/// preorder.committed) legitimately stay as direct <c>AuditEvent</c> writes
/// inside their handlers' transactions — the DbContext-level
/// <c>AuditEventSanitizationInterceptor</c> gives every row Inv-12 metadata
/// hygiene regardless of write path. New SECURITY-decision events must come
/// through this writer.
/// </summary>
public sealed class AuditWriter(IAuditEventRepository auditRepo) : IAuditWriter
{
    public async Task WriteAsync(AuditRecord record, CancellationToken ct = default)
        => await auditRepo.AddAsync(AuditEnvelope.Build(record), ct);
}
