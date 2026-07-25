namespace Cordia.Domain.Exceptions;

/// <summary>
/// Thrown at channel ingress (spec-15 §7, Inv-13) when a channel-controlled signal resolves to
/// MORE THAN ONE active <c>tenant_channel_endpoints</c> row. The <c>uq_channel_signal_active</c>
/// partial-unique index makes this structurally impossible under normal operation; if it is ever
/// observed the gateway fails closed rather than guess a tenant (§30 fail-closed, §25.4 alert).
///
/// The message is a fixed, generic string — it must NEVER contain the channel signal value or any
/// tenant identifier (Inv-12). Mapped by the GlobalExceptionHandler to a channel-appropriate 4xx,
/// never a 500 and never a fallback tenant.
/// </summary>
public sealed class AmbiguousEndpointException()
    : DomainException("The inbound signal matched more than one active channel endpoint.");
