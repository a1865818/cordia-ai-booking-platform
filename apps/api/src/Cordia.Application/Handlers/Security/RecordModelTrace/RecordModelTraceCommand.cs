using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Security.RecordModelTrace;

/// <summary>
/// Persists one model prompt/output into the encrypted object store under the STRICTER
/// telemetry retention tier (spec 13 §25.3 via spec 14's storage boundary): purpose
/// <c>"model-trace"</c>, 14d TTL (GD-3 — strictly shorter than transcripts), never app-logged.
/// Content is redacted agent-side (<c>redact_text</c>) and re-sanitized here (Inv-12).
/// Telemetry WRITES are not audited (high-volume data plane); every VIEW is — see
/// <c>GetModelTraceQuery</c> (the spec 13 §4.3 "who viewed model traces" append).
/// </summary>
public sealed record RecordModelTraceCommand(
    string TenantId,
    string TraceId,
    string? SessionId,
    string Kind,
    string Content) : IRequest<ModelTraceRecordedResult>;

/// <summary>Write acknowledgement.</summary>
public sealed record ModelTraceRecordedResult(string TraceId, bool Stored);

/// <summary>Structural rules for the trace write.</summary>
public sealed class RecordModelTraceValidator : AbstractValidator<RecordModelTraceCommand>
{
    public RecordModelTraceValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.TraceId).NotEmpty();
        RuleFor(x => x.Kind).NotEmpty()
            .Must(k => k is "prompt" or "output")
            .WithMessage("Kind must be 'prompt' or 'output'.");
        RuleFor(x => x.Content).NotEmpty();
    }
}
