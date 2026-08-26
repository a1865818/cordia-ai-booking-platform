using System.Text.RegularExpressions;
using Cordia.Application.Common.Audit;
using Cordia.Application.Handlers.ConversationSessions.CreateConversationSession;
using Cordia.Domain.Entities;
using Cordia.Domain.Exceptions;
using Cordia.Domain.Interfaces;
using Cordia.Domain.TenantResolution;
using Cordia.Infrastructure.Services;
using MediatR;
using Microsoft.Extensions.Options;

namespace Cordia.Application.Handlers.TenantResolution.ResolveTenantContext;

/// <summary>
/// Resolves the channel signal to exactly one active endpoint and mints a signed
/// <c>TenantContext</c> (spec-15 §7, Inv-13). Fail-closed at every branch, and every outcome
/// (success + each failure) appends an immutable <c>tenant.resolution</c> audit row (§24.5) —
/// failed resolutions have no session, so the audit is their only durable record.
/// </summary>
public sealed partial class ResolveTenantContextCommandHandler(
    ITenantChannelEndpointRepository endpoints,
    ITenantContextSigner signer,
    IMediator mediator,
    IAuditWriter audit,
    ICorrelationContext correlation,
    IOptions<TenantContextOptions> options,
    TimeProvider timeProvider)
    : IRequestHandler<ResolveTenantContextCommand, SignedTenantContext>
{
    private const string EventType = "tenant.resolution";

    // Safe shape for an echoed inbound provider correlation id — mirrors CorrelationMiddleware's
    // allowlist (bounded, no control chars/delimiters) so a hostile value can't be echoed into logs.
    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCorrelationId();

    public async Task<SignedTenantContext> Handle(ResolveTenantContextCommand command, CancellationToken ct)
    {
        // Capture the clock once so the signed claims (issued_at/expires_at) and EVERY audit row from
        // this resolution share one timestamp (audit.OccurredAt never drifts from claims.issued_at).
        var now = timeProvider.GetUtcNow();

        // Originate the correlation id once, before resolution, so EVERY outcome (incl. failures) is
        // audited under one id (§25.1 — echo a valid inbound provider id, else the ambient id, else
        // mint once; never re-mint).
        var correlationId = ResolveCorrelationId(command.InboundCorrelationId);

        // Resolve to exactly one active endpoint (Inv-13): 0 ⇒ tenant_not_resolved, >1 ⇒ ambiguous.
        var matches = await endpoints.ResolveAllActiveAsync(
            command.Channel, command.SignalType, command.SignalValue, ct);

        if (matches.Count == 0)
        {
            await AuditAsync(command, "tenant_not_resolved", tenantId: string.Empty,
                channelEndpointId: string.Empty, correlationId, now, ct);
            throw new TenantNotResolvedException();
        }

        if (matches.Count > 1)
        {
            await AuditAsync(command, "ambiguous_endpoint", tenantId: string.Empty,
                channelEndpointId: string.Empty, correlationId, now, ct);
            throw new AmbiguousEndpointException();
        }

        var endpoint = matches[0];

        // Pin the config version + policy ids and mint the claim set. session_id is allocated here
        // (the gateway owns it, D12); issued_at/expires_at come from the captured clock + TTL.
        var sessionId = $"sess_{Guid.NewGuid():N}";
        var policyIds = DerivePolicyIds(endpoint);

        var claims = new TenantContextClaims(
            TenantId: endpoint.TenantId,
            Channel: endpoint.Channel,
            ChannelEndpointId: endpoint.ChannelEndpointId,
            ConfigurationVersion: endpoint.ConfigurationVersion,
            CorrelationId: correlationId,
            SessionId: sessionId,
            AllowedAgentPolicyIds: policyIds,
            AllowedToolPolicyIds: policyIds,
            IssuedAt: now,
            ExpiresAt: now + TimeSpan.FromSeconds(options.Value.TtlSeconds));

        // Sign — fail closed on any signing failure (no context returned), audited.
        string jws;
        try
        {
            jws = signer.Sign(claims);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AuditAsync(command, "signing_failed", endpoint.TenantId, endpoint.ChannelEndpointId,
                correlationId, now, ct);
            throw;
        }

        // Create the tenant-scoped session row via the single-writer command (D12) — fail closed on
        // an insert failure (no context returned), audited.
        try
        {
            await mediator.Send(
                new CreateConversationSessionCommand(
                    endpoint.TenantId, sessionId, endpoint.Channel, endpoint.ChannelEndpointId, correlationId),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await AuditAsync(command, "session_insert_failed", endpoint.TenantId, endpoint.ChannelEndpointId,
                correlationId, now, ct);
            throw;
        }

        // Success — audit the resolution, then hand back the signed context.
        await AuditAsync(command, "resolved", endpoint.TenantId, endpoint.ChannelEndpointId, correlationId, now, ct);

        return new SignedTenantContext(jws, claims);
    }

    private string ResolveCorrelationId(string? inbound)
    {
        // Echo a well-formed inbound provider correlation id (§25.1 — never mint a second).
        if (!string.IsNullOrEmpty(inbound) && SafeCorrelationId().IsMatch(inbound))
            return inbound;

        // Otherwise reuse the ambient id the ingress correlation middleware already established;
        // only when there is none (e.g. queue-driven paths) mint exactly one.
        var ambient = correlation.Current.CorrelationId;
        return string.IsNullOrEmpty(ambient) ? $"corr_{Guid.NewGuid():N}" : ambient;
    }

    /// <summary>
    /// FLAGGED DEVIATION (spec-15 Phase 3): §5.1 splits authorization into
    /// <c>allowed_agent_policy_ids</c> + <c>allowed_tool_policy_ids</c>, expanded from a policy
    /// bundle by the "configuration-version resolver" (§7). That resolver / gated
    /// <c>tenant_configuration</c> surface is out of scope here (deferred). The registry row carries
    /// only a single opaque <see cref="TenantChannelEndpoint.PolicyBundleId"/>, so — until the
    /// expander lands — BOTH claim lists carry the pinned bundle id as the single policy reference
    /// (empty bundle ⇒ empty lists). This preserves the pinned authorization for the downstream
    /// resolver without inventing schema.
    /// TODO(#79-followup, spec-15/spec-03): replace with the real bundle → {agent,tool} policy-id expansion.
    /// </summary>
    private static IReadOnlyList<string> DerivePolicyIds(TenantChannelEndpoint endpoint)
        => string.IsNullOrEmpty(endpoint.PolicyBundleId)
            ? []
            : [endpoint.PolicyBundleId];

    private Task AuditAsync(
        ResolveTenantContextCommand command,
        string outcome,
        string tenantId,
        string channelEndpointId,
        string correlationId,
        DateTimeOffset occurredAt,
        CancellationToken ct)
        => audit.WriteAsync(
            new AuditRecord(
                TenantId: tenantId,
                ActorType: "system",
                ActorId: "channel-gateway",
                Action: EventType,
                TargetType: "tenant_channel_endpoint",
                TargetId: channelEndpointId,
                Decision: outcome == "resolved" ? "allow" : "deny",
                DataClasses: [],
                CorrelationId: correlationId,
                OccurredAt: occurredAt,
                Extra: new
                {
                    signal_type = command.SignalType,
                    channel = command.Channel,
                    channel_endpoint_id = channelEndpointId,
                    tenant_id = tenantId,
                    outcome,
                }),
            ct);
}
