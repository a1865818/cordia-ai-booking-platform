using System.Diagnostics;
using Cordia.Application.Messaging;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Observability;
using Cordia.Infrastructure.Observability;
using MediatR;
using Microsoft.Extensions.Logging;
using Serilog.Context;

namespace Cordia.Application.Observability;

/// <summary>
/// MediatR pipeline behaviour that threads the ambient correlation context through every handler
/// (§25.1). At the HTTP edge the <c>CorrelationMiddleware</c> has already set the context; for
/// queue-driven requests (<see cref="IInboxConsumerRequest"/>) there is no HTTP edge, so when the
/// ambient context is missing this behaviour seeds it FROM the message (correlation_id, tenant_id,
/// causation_id = message id) — preserving lineage across the broker (§25.1).
///
/// <para>For the duration of the handler it pushes the correlation fields onto the current
/// <see cref="Activity"/> tags AND the Serilog <see cref="LogContext"/>. If no correlation id can
/// be established it increments the §8 uncorrelated-record counter and continues (fail-open — it
/// never throws on the business path).</para>
///
/// <para>Registered AFTER <c>InboxDeduplicationBehavior</c> so the inbox transaction/admission has
/// already classified the message; order is Validation → InboxDedup → CorrelationEnrichment →
/// handler.</para>
/// </summary>
public sealed class CorrelationEnrichmentBehavior<TRequest, TResponse>(
    ICorrelationContext correlation,
    ITenantContext tenantContext,
    Metrics metrics,
    ILogger<CorrelationEnrichmentBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var ctx = correlation.Current;
        AmbientCorrelationContext? prior = null;

        if (request is IInboxConsumerRequest inbox)
        {
            // Queue-driven: always override (not just when empty) so successive messages in the same
            // background-service loop never inherit the previous message's correlation/causation ids (§25.1).
            prior = ctx;
            ctx = new AmbientCorrelationContext(
                TenantId: inbox.TenantId,
                CorrelationId: inbox.CorrelationId ?? string.Empty,
                CausationId: inbox.MessageId,
                OperationId: inbox.OperationId);
            correlation.Set(ctx);
        }
        else if (string.IsNullOrEmpty(ctx.TenantId) && !string.IsNullOrEmpty(tenantContext.TenantId))
        {
            // HTTP-driven: CorrelationMiddleware runs before TenantResolutionMiddleware and sets
            // TenantId = "" as a placeholder. Fold in the now-resolved tenant so all handler
            // logs and activity tags carry tenant_id (§25.1).
            ctx = ctx with { TenantId = tenantContext.TenantId };
            correlation.Set(ctx);
        }

        try
        {
            if (string.IsNullOrEmpty(ctx.CorrelationId))
            {
                // §8 fail-open: make the gap visible rather than throwing. A queue message reaching here
                // with no correlation_id is a §25.1 contract violation worth surfacing for investigation.
                metrics.TelemetryUncorrelatedRecordTotal.Add(1);
                if (request is IInboxConsumerRequest uncorrelated)
                {
                    logger.LogWarning(
                        "Uncorrelated queue message {MessageId} (consumer {Consumer}): missing correlation_id — §25.1 contract violation",
                        uncorrelated.MessageId,
                        typeof(TRequest).Name);
                }

                return await next(cancellationToken);
            }

            var activity = Activity.Current;
            activity?.SetTag("correlation_id", ctx.CorrelationId);
            if (!string.IsNullOrEmpty(ctx.TenantId))
                activity?.SetTag("tenant_id", ctx.TenantId);
            if (!string.IsNullOrEmpty(ctx.CausationId))
                activity?.SetTag("causation_id", ctx.CausationId);
            if (!string.IsNullOrEmpty(ctx.OperationId))
                activity?.SetTag("operation_id", ctx.OperationId);

            // Push only the fields that carry a value, so log lines aren't padded with empty properties.
            using var p1 = LogContext.PushProperty("correlation_id", ctx.CorrelationId);
            using var p2 = string.IsNullOrEmpty(ctx.TenantId)
                ? (IDisposable?)null : LogContext.PushProperty("tenant_id", ctx.TenantId);
            using var p3 = string.IsNullOrEmpty(ctx.CausationId)
                ? (IDisposable?)null : LogContext.PushProperty("causation_id", ctx.CausationId);
            using var p4 = string.IsNullOrEmpty(ctx.OperationId)
                ? (IDisposable?)null : LogContext.PushProperty("operation_id", ctx.OperationId);

            return await next(cancellationToken);
        }
        finally
        {
            // Restore the pre-handler ambient context so the background-service loop's AsyncLocal state
            // doesn't carry this message's ids into the next iteration (§25.1).
            if (prior is not null)
                correlation.Set(prior);
        }
    }
}
