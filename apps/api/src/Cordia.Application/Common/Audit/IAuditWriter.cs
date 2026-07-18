namespace Cordia.Application.Common.Audit;

/// <summary>
/// The spec-14 audit write path (D13). Appends an immutable audit_events row;
/// callers performing a durable effect MUST invoke this inside the same
/// transaction as the effect (spec 14 §3.1 step 5). Metadata is sanitized —
/// PAN-shaped values never reach the audit trail (Inv-12).
/// </summary>
public interface IAuditWriter
{
    Task WriteAsync(AuditRecord record, CancellationToken ct = default);
}
