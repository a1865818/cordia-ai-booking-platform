using System.Text.Json;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.Security.GetModelTrace;

/// <summary>
/// Role check first (deny audited + 403), then fetch; the allow audit records WHO viewed the
/// trace (spec 13 §4.3) with the asserted role, reason code, and whether it was found.
/// </summary>
public sealed class GetModelTraceQueryHandler(
    IObjectStore store,
    IAuditWriter audit)
    : IRequestHandler<GetModelTraceQuery, ModelTraceView?>
{
    private const string Purpose = "model-trace";
    private const string Action = "model_trace.viewed";

    public async Task<ModelTraceView?> Handle(GetModelTraceQuery query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.ActorId)
            || string.IsNullOrWhiteSpace(query.ReasonCode)
            || !StaffRole.Satisfies(query.ActorRole, StaffRole.Operator))
        {
            await WriteAuditAsync(query, decision: "deny", found: null);
            throw new AuthorizationFailedException(
                Action,
                "Viewing a model trace requires a staff role (operator or owner) with an actor identity and a reason code (spec 13 §25.3 / spec 14 §3.1).");
        }

        var bytes = await store.GetAsync(query.TenantId, Purpose, query.TraceId, ct);

        // The view attempt itself is audited whether or not the trace exists (§4.3).
        await WriteAuditAsync(query, decision: "allow", found: bytes is not null);

        if (bytes is null)
            return null;

        using var doc = JsonDocument.Parse(bytes);
        return new ModelTraceView(
            TraceId: query.TraceId,
            Kind: doc.RootElement.GetProperty("kind").GetString() ?? string.Empty,
            SessionId: doc.RootElement.TryGetProperty("session_id", out var sid)
                ? sid.GetString()
                : null,
            Content: doc.RootElement.GetProperty("content").GetString() ?? string.Empty,
            Ts: doc.RootElement.GetProperty("ts").GetDateTimeOffset());
    }

    // Audits are written on CancellationToken.None (B2) so the record — a DENY refusal or the
    // §4.3 view record — is durable even if the client disconnects and cancels the request token.
    private Task WriteAuditAsync(
        GetModelTraceQuery query, string decision, bool? found)
        => audit.WriteAsync(new AuditRecord(
            TenantId: query.TenantId,
            ActorType: StaffRole.IsKnown(query.ActorRole) ? query.ActorRole! : "operator",
            ActorId: string.IsNullOrWhiteSpace(query.ActorId) ? "unknown" : query.ActorId,
            Action: Action,
            TargetType: "model_trace",
            TargetId: query.TraceId,
            Decision: decision,
            DataClasses: ["model_trace"],
            ReasonCode: string.IsNullOrWhiteSpace(query.ReasonCode) ? null : query.ReasonCode,
            CorrelationId: query.TraceId,
            Extra: found is null ? new { } : new { found }), CancellationToken.None);
}
