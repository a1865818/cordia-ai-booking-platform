using System.Security.Cryptography;
using System.Text;
using Cordia.Application.Handlers.Notifications.RecordDeliveryReceipt;
using Cordia.Domain.Interfaces;
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
///   - X-Webhook-Secret header must match Notifications:WebhookSecret config value.
///     Production HMAC signature verification is deferred (§31 open decisions).
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
    ITenantContext tenant,
    IConfiguration config) : ControllerBase
{
    /// <summary>
    /// Records a delivery receipt from the notification provider.
    /// Transitions Accepted → Delivered (positive) or Accepted → FailedPermanent (bounce).
    /// Idempotent on (notification_id, provider_message_id).
    /// </summary>
    [HttpPost("delivery-receipts")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RecordDeliveryReceipt(
        [FromBody] DeliveryReceiptRequest body,
        CancellationToken ct)
    {
        // Auth: tenant header required.
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        // Auth: webhook secret — constant-time comparison to prevent timing-based secret oracle.
        // TODO (§31): replace shared-secret check with full HMAC signature verification before production.
        var configuredSecret = config["Notifications:WebhookSecret"];
        if (string.IsNullOrEmpty(configuredSecret))
            return Unauthorized();

        var providedSecret = Request.Headers["X-Webhook-Secret"].ToString();
        if (string.IsNullOrEmpty(providedSecret))
            return Unauthorized();

        // FixedTimeEquals requires equal-length spans. We compare the UTF-8 byte representations;
        // if the lengths differ we still perform a dummy fixed-time compare against the configured
        // secret bytes to avoid leaking length information via a detectable early-return, then
        // always return 401 for the length-mismatch case.
        var configuredBytes = Encoding.UTF8.GetBytes(configuredSecret);
        var providedBytes = Encoding.UTF8.GetBytes(providedSecret);
        var lengthMatch = configuredBytes.Length == providedBytes.Length;
        // Always call FixedTimeEquals: on length mismatch compare configured against itself
        // (result is irrelevant; we discard it) so the call site does not short-circuit.
        var contentMatch = CryptographicOperations.FixedTimeEquals(
            configuredBytes,
            lengthMatch ? providedBytes : configuredBytes);
        if (!lengthMatch || !contentMatch)
            return Unauthorized();

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
