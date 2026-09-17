using Cordia.Api.Security;
using Cordia.Application.Handlers.Notifications.RecordDeliveryReceipt;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Webhook endpoint for provider delivery receipts (spec 10 §6.1, T3).
///
/// POST /api/notification-webhooks/delivery-receipts
///
/// Auth:
///   - X-Tenant-Id header is required (resolved by TenantResolutionMiddleware, validated here).
///   - X-Webhook-Timestamp + X-Webhook-Signature: an HMAC-SHA256 over the timestamp, tenant and
///     raw body, keyed by the shared secret from the spec-14 custody seam (<c>ISecretStore</c>, key
///     <c>NotificationsWebhookSecret</c>, legacy <c>Notifications:WebhookSecret</c> fallback), inside a
///     five-minute window (#74). See <see cref="WebhookSignature"/>. The plain X-Webhook-Secret
///     header is no longer accepted.
///
/// Outcome mapping:
///   NotFound          → 404
///   Applied           → 202  (idempotent)
///   AlreadyTerminal   → 202  (idempotent — already in the desired terminal state)
///   Mismatch          → 202  (stale/foreign receipt; no state change; ack so provider won't retry)
///   OutOfOrder        → 202  (receipt arrived before Accepted; ack without state change)
/// </summary>
[ApiController]
[Route("api/notification-webhooks")]
public class NotificationWebhooksController(
    IMediator mediator,
    ITenantContext tenant) : ControllerBase
{
    /// <summary>
    /// Records a delivery receipt from the notification provider.
    /// Transitions Accepted → Delivered (positive) or Accepted → FailedPermanent (bounce).
    /// Idempotent on (notification_id, provider_message_id).
    /// </summary>
    [HttpPost("delivery-receipts")]
    [VerifyWebhookSignature]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> RecordDeliveryReceipt(
        [FromBody] DeliveryReceiptRequest body,
        CancellationToken ct)
    {
        // Auth: the HMAC signature and timestamp were verified by [VerifyWebhookSignature] before
        // this body was bound. The tenant it was signed for is the one required here.
        TenantGuard.RequireTenant(tenant);

        // Map status string (case-insensitive) → enum.
        DeliveryReceiptStatus status;
        if (string.Equals(body.Status, "delivered", StringComparison.OrdinalIgnoreCase))
            status = DeliveryReceiptStatus.Delivered;
        else if (string.Equals(body.Status, "bounced", StringComparison.OrdinalIgnoreCase))
            status = DeliveryReceiptStatus.Bounced;
        else
            return Problem(statusCode: 400, title: "Invalid status",
                detail: $"Status must be 'delivered' or 'bounced', got '{body.Status}'.");

        var outcome = await mediator.Send(
            new RecordDeliveryReceiptCommand(
                tenant.TenantId,
                body.NotificationId,
                body.ProviderMessageId,
                status),
            ct);

        return outcome switch
        {
            ReceiptOutcome.NotFound => NotFound(),
            // Applied, AlreadyTerminal, Mismatch, OutOfOrder → all 202 (idempotent ack).
            _ => Accepted(),
        };
    }
}

/// <summary>Request body for POST /api/notification-webhooks/delivery-receipts.</summary>
public sealed record DeliveryReceiptRequest(
    [property: System.ComponentModel.DataAnnotations.Required]
    [property: System.Text.Json.Serialization.JsonPropertyName("notification_id")]
    string NotificationId,
    [property: System.ComponentModel.DataAnnotations.Required]
    [property: System.Text.Json.Serialization.JsonPropertyName("provider_message_id")]
    string ProviderMessageId,
    [property: System.ComponentModel.DataAnnotations.Required]
    [property: System.Text.Json.Serialization.JsonPropertyName("status")]
    string Status)
{
    // Parameterless ctor for model binding when optional fields are absent.
    public DeliveryReceiptRequest() : this(string.Empty, string.Empty, string.Empty) { }
}
