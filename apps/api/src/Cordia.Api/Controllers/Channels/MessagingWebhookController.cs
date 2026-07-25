using System.Text.Json.Serialization;
using Cordia.Application.Handlers.TenantResolution.ResolveTenantContext;
using Cordia.Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers.Channels;

/// <summary>
/// Messaging-channel ingress (spec-15 §7, Inv-13). A messaging provider posts an inbound-message
/// webhook; the gateway resolves the tenant from the <b>recipient / page / business id</b> (the
/// transport-attested destination the message was addressed to) and returns a signed short-lived
/// <c>TenantContext</c>.
///
/// <para>POST <c>api/channels/messaging</c></para>
///
/// <para><b>Inv-13 — transport-attested signal ONLY.</b> Resolution uses <c>recipient_id</c> (the
/// business/page the customer messaged) and nothing else. The sender id (<c>sender_id</c>) and the
/// sender display name (<c>sender_name</c>) are caller-mutable and must NEVER influence which tenant
/// is reached; they are accepted for provider-payload shape only and are deliberately never read.</para>
///
/// <para>Thin controller — no <c>TenantGuard.RequireTenant</c> (the tenant is the OUTPUT). Outcomes
/// are mapped by the GlobalExceptionHandler: no active endpoint / ambiguous ⇒ 422 (identical generic
/// body); empty signal ⇒ 400 (FluentValidation).</para>
/// </summary>
[ApiController]
[Route("api/channels/messaging")]
public class MessagingWebhookController(IMediator mediator) : ControllerBase
{
    /// <summary>
    /// Documented provider header carrying the provider's own message correlation id. Echoed as the
    /// resolution <c>correlation_id</c> when well-formed (<c>^[A-Za-z0-9_-]{1,64}$</c>); otherwise the
    /// ingress correlation id minted by <c>CorrelationMiddleware</c> is used (§25.1 — never re-minted).
    /// </summary>
    public const string ProviderCorrelationHeader = "X-Provider-Message-Id";

    /// <summary>Resolves the tenant for an inbound message and mints its signed <c>TenantContext</c>.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(TenantContextResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<TenantContextResponse>> Resolve(
        [FromBody] MessagingWebhookRequest body,
        [FromHeader(Name = ProviderCorrelationHeader)] string? providerCorrelationId,
        CancellationToken ct)
    {
        // Inv-13: resolve ONLY from the transport-attested recipient/page/business id
        // (body.RecipientId). body.SenderId and body.SenderName are caller-mutable and are
        // intentionally NOT passed to the resolver.
        var signed = await mediator.Send(
            new ResolveTenantContextCommand(
                TenantChannel.Messaging,
                SignalType.MessagingRecipient,
                body.RecipientId ?? string.Empty,
                providerCorrelationId),
            ct);

        return Ok(TenantContextResponse.From(signed));
    }
}

/// <summary>
/// Inbound messaging webhook body. Only <see cref="RecipientId"/> (the transport-attested destination
/// page/business id) drives resolution; <see cref="SenderId"/> and <see cref="SenderName"/> are
/// present for provider-payload shape and are NEVER used to resolve a tenant (Inv-13 — caller-mutable).
/// </summary>
public sealed record MessagingWebhookRequest(
    [property: JsonPropertyName("recipient_id")] string? RecipientId,
    [property: JsonPropertyName("sender_id")] string? SenderId,
    [property: JsonPropertyName("sender_name")] string? SenderName)
{
    // Parameterless ctor for model binding when optional fields are absent.
    public MessagingWebhookRequest() : this(null, null, null) { }
}
