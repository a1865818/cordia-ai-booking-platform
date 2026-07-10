using System.Text.Json;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;

namespace Cordia.Application.Handlers.Security.RecordModelTrace;

/// <summary>
/// Stores the sanitized trace as one encrypted object; the store applies the GD-3
/// model-trace TTL (14d) and the retention sweep prunes + audits removals. Idempotent per
/// trace id (re-post overwrites).
/// </summary>
public sealed class RecordModelTraceCommandHandler(IObjectStore store)
    : IRequestHandler<RecordModelTraceCommand, ModelTraceRecordedResult>
{
    public const string Purpose = "model-trace";

    public async Task<ModelTraceRecordedResult> Handle(
        RecordModelTraceCommand command, CancellationToken ct)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = command.Kind,
            session_id = command.SessionId,
            // Inv-12 defense-in-depth: the agent redacts pre-emit; mask again at the boundary.
            content = SensitiveDataMask.Sanitize(command.Content),
            ts = DateTimeOffset.UtcNow,
        });

        await store.PutAsync(
            command.TenantId, Purpose, command.TraceId, content,
            ttl: null /* store applies the GD-3 model-trace default (strictly < transcript) */, ct);

        return new ModelTraceRecordedResult(command.TraceId, Stored: true);
    }
}
