namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown at channel ingress (spec-15 §7, Inv-13) when a channel-controlled signal resolves to
/// ZERO active <c>tenant_channel_endpoints</c> rows. The gateway fails closed — no session, no
/// context, never a fallback/default tenant (§30 fail-closed).
///
/// The message is a fixed, generic string — it must NEVER contain the channel signal value or any
/// tenant identifier (Inv-12). Mapped by the GlobalExceptionHandler to a channel-appropriate 4xx
/// ("can't reach this business"), never a 500.
/// </summary>
public sealed class TenantNotResolvedException()
    : DomainException("No active channel endpoint matched the inbound signal.");
