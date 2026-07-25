using Cordia.Domain.Enums;
using Cordia.Domain.TenantResolution;
using MediatR;

namespace Cordia.Application.Handlers.TenantResolution.ResolveTenantContext;

/// <summary>
/// The spec-15 channel-gateway core (§7, Inv-13): resolve a channel-controlled signal to EXACTLY
/// ONE active endpoint, pin the configuration version + policy ids, originate/echo the
/// <c>correlation_id</c>, allocate a <c>session_id</c>, mint + sign a short-lived
/// <c>TenantContext</c>, create the <c>conversation_sessions</c> row (D12), and audit every outcome.
///
/// Fails closed — zero matches ⇒ <see cref="Cordia.Domain.Exceptions.TenantNotResolvedException"/>,
/// many ⇒ <see cref="Cordia.Domain.Exceptions.AmbiguousEndpointException"/>; a signing or
/// session-insert failure rethrows after auditing. NEVER a fallback/default tenant.
///
/// <see cref="InboundCorrelationId"/> is an OPTIONAL provider-supplied correlation id from the
/// webhook — echoed when present and well-formed (§25.1), never used to mint a second id.
/// </summary>
public sealed record ResolveTenantContextCommand(
    TenantChannel Channel,
    SignalType SignalType,
    string SignalValue,
    string? InboundCorrelationId) : IRequest<SignedTenantContext>;

/// <summary>
/// The gateway's output: the compact HS256 JWS and the decoded claim set it carries. Handed to the
/// channel session (voice pipeline / agent) which verifies the JWS before any data access (Inv-13).
/// </summary>
public sealed record SignedTenantContext(string Jws, TenantContextClaims Claims);
