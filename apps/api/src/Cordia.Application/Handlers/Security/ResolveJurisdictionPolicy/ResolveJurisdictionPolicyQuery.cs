using Cordia.Domain.Security;
using MediatR;

namespace Cordia.Application.Handlers.Security.ResolveJurisdictionPolicy;

/// <summary>
/// Resolves the recording-consent policy for (tenant, channel) — spec 14 §5.1.
/// <paramref name="Channel"/> is the wire channel value from the agent
/// ("voice" | "sms" | "messaging" | "web" | "email"); "sms" normalizes to
/// the messaging channel.
/// </summary>
public sealed record ResolveJurisdictionPolicyQuery(
    string TenantId,
    string Channel) : IRequest<JurisdictionPolicy>;
