using System.Text.Json.Serialization;
using Cordia.Application.Handlers.TenantResolution.ResolveTenantContext;
using Cordia.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers.Channels;

/// <summary>
/// Voice-channel ingress (spec-15 §7, Inv-13). A telephony provider posts an inbound-call webhook;
/// the gateway resolves the tenant from the <b>called DID</b> (the transport-attested destination
/// number the carrier routed on) and returns a signed short-lived <c>TenantContext</c>.
///
/// <para>POST <c>api/channels/telephony</c></para>
///
/// <para><b>Inv-13 — transport-attested signal ONLY.</b> Resolution uses <c>to</c> (the called DID)
/// and nothing else. The caller's number (<c>from</c>/ANI) and especially the spoken/SIP display name
/// (<c>caller_name</c>) are caller-mutable — a spoofed display name that contradicts the DID must
/// NEVER change which tenant is reached. Those fields are accepted for provider-payload shape only and
/// are deliberately never read here.</para>
///
/// <para>This controller is deliberately THIN: no <c>TenantGuard.RequireTenant</c> (the tenant is the
/// OUTPUT of this endpoint, not a precondition). Outcomes are mapped by the GlobalExceptionHandler:
/// no active endpoint ⇒ <c>TenantNotResolvedException</c> ⇒ 422; ambiguous ⇒
/// <c>AmbiguousEndpointException</c> ⇒ 422 (same generic body — never reveal which); an empty signal
/// ⇒ FluentValidation ⇒ 400.</para>
/// </summary>
[ApiController]
[Route("api/channels/telephony")]
public class TelephonyWebhookController(IMediator mediator) : ControllerBase
{
    /// <summary>
    /// Documented provider header carrying the provider's own call correlation id (e.g. the carrier
    /// call SID). Echoed as the resolution <c>correlation_id</c> when it is well-formed
    /// (<c>^[A-Za-z0-9_-]{1,64}$</c>, enforced by the resolution handler); otherwise the ingress
    /// correlation id minted by <c>CorrelationMiddleware</c> is used (§25.1 — never re-minted).
    /// </summary>
    public const string ProviderCorrelationHeader = "X-Provider-Call-Id";

    /// <summary>Resolves the tenant for an inbound voice call and mints its signed <c>TenantContext</c>.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(TenantContextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TenantContextResponse>> Resolve(
        [FromBody] TelephonyWebhookRequest body,
        [FromHeader(Name = ProviderCorrelationHeader)] string? providerCorrelationId,
        CancellationToken ct)
    {
        // Inv-13: resolve ONLY from the transport-attested called DID (body.To). body.From (caller ANI)
        // and body.CallerName (spoken / SIP display name) are caller-mutable and are intentionally NOT
        // passed to the resolver — they must never influence which tenant is reached.
        var signed = await mediator.Send(
            new ResolveTenantContextCommand(
                TenantChannel.Voice,
                SignalType.PhoneNumber,
                body.To ?? string.Empty,
                providerCorrelationId),
            ct);

        return Ok(TenantContextResponse.From(signed));
    }
}

/// <summary>
/// Inbound telephony webhook body. Only <see cref="To"/> (the transport-attested called DID) drives
/// resolution; <see cref="From"/> and <see cref="CallerName"/> are present for provider-payload shape
/// and are NEVER used to resolve a tenant (Inv-13 — caller-mutable).
/// </summary>
public sealed record TelephonyWebhookRequest(
    [property: JsonPropertyName("to")] string? To,
    [property: JsonPropertyName("from")] string? From,
    [property: JsonPropertyName("caller_name")] string? CallerName)
{
    // Parameterless ctor for model binding when optional fields are absent.
    public TelephonyWebhookRequest() : this(null, null, null) { }
}
