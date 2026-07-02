using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Waitlist.Accept;
using Cordia.Application.Handlers.Waitlist.Cancel;
using Cordia.Application.Handlers.Waitlist.Decline;
using Cordia.Application.Handlers.Waitlist.Enroll;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Waitlist;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Customer/owner-facing endpoints for the waitlist (spec 11 §4.4, §5.1).
///
/// POST   /api/waitlist/enrollments              → 201 (enroll)
/// DELETE /api/waitlist/enrollments/{id}         → 200 | 404 (cancel; idempotent)
/// POST   /api/waitlist/offers/{offerId}/accept  → 200 | 404 | 409
/// POST   /api/waitlist/offers/{offerId}/decline → 200 | 404 | 409
///
/// Thin by design: resolves tenant (X-Tenant-Id) + actor (X-Actor), sends a MediatR command, and
/// maps typed domain exceptions via the GlobalExceptionHandler (offer/enrollment not found → 404,
/// illegal offer transition → 409). Authorization is a TODO; every action is audited with the actor.
/// </summary>
[ApiController]
[Route("api/waitlist")]
public class WaitlistController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>Enrolls a customer on the waitlist. Returns 201 with the new enrollment id.</summary>
    [HttpPost("enrollments")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Enroll(
        [FromBody] EnrollWaitlistRequest body,
        [FromHeader(Name = "X-Actor")] string? actor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        var deposit = body.DepositTerms is { } d ? new DepositTerms(d.AmountMinor, d.Currency) : null;
        var autoBook = body.AutoBook is { } a
            ? new AutoBookGrant(
                a.ConsentEvidenceId, a.ConsentedAt, a.MaxPartySize,
                a.ConstraintFrom, a.ConstraintTo, a.MaxDepositAmountMinor, a.DepositCurrency)
            : null;

        var enrollmentId = await mediator.Send(
            new EnrollWaitlistCommand(
                tenant.TenantId, ResolveActor(actor), body.CustomerRef,
                body.AcceptableFrom, body.AcceptableTo, body.PartySize,
                body.LocationId, body.ServiceId, body.OfferLifetimeSeconds,
                deposit, body.NotificationChannel, autoBook,
                body.IdentityRef, body.VerificationLevel), ct);

        return Created($"/api/waitlist/enrollments/{enrollmentId}",
            new { enrollment_id = enrollmentId, status = "enrolled" });
    }

    /// <summary>Cancels an enrollment (terminal, idempotent). Releases any live offer's hold.</summary>
    [HttpDelete("enrollments/{enrollmentId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(
        string enrollmentId,
        [FromHeader(Name = "X-Actor")] string? actor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        await mediator.Send(new CancelWaitlistEnrollmentCommand(tenant.TenantId, enrollmentId, ResolveActor(actor)), ct);
        return Ok(new { enrollment_id = enrollmentId, status = "cancelled" });
    }

    /// <summary>Accepts a live offer (finalizes the hold + emits a booking-confirmed notification).</summary>
    [HttpPost("offers/{offerId}/accept")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Accept(
        string offerId,
        [FromHeader(Name = "X-Actor")] string? actor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        await mediator.Send(new AcceptWaitlistOfferCommand(tenant.TenantId, offerId, ResolveActor(actor)), ct);
        return Ok(new { offer_id = offerId, status = "accepted" });
    }

    /// <summary>Declines a live offer (releases the hold + re-matches the next eligible enrollee).</summary>
    [HttpPost("offers/{offerId}/decline")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Decline(
        string offerId,
        [FromHeader(Name = "X-Actor")] string? actor,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenant.TenantId))
            return Problem(statusCode: 400, title: "Missing tenant", detail: "X-Tenant-Id header is required.");

        await mediator.Send(new DeclineWaitlistOfferCommand(tenant.TenantId, offerId, ResolveActor(actor)), ct);
        return Ok(new { offer_id = offerId, status = "declined" });
    }

    private static string ResolveActor(string? actor)
        => string.IsNullOrWhiteSpace(actor) ? "customer" : actor;
}

/// <summary>Body for POST /api/waitlist/enrollments.</summary>
public sealed record EnrollWaitlistRequest(
    [property: JsonPropertyName("customer_ref")] string CustomerRef,
    [property: JsonPropertyName("acceptable_from")] DateTimeOffset AcceptableFrom,
    [property: JsonPropertyName("acceptable_to")] DateTimeOffset AcceptableTo,
    [property: JsonPropertyName("party_size")] int PartySize,
    [property: JsonPropertyName("location_id")] string LocationId,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("offer_lifetime_seconds")] int OfferLifetimeSeconds,
    [property: JsonPropertyName("notification_channel")] string NotificationChannel,
    [property: JsonPropertyName("deposit_terms")] DepositTermsDto? DepositTerms,
    [property: JsonPropertyName("auto_book")] AutoBookGrantDto? AutoBook,
    // §5.1 "verified follow-up": the identity/assurance captured at enrollment binds the eventual
    // accept's confirmation. Without these the enrollment is stamped unverified — the DTO
    // previously had no way to carry them (review 2026-07-03, C9).
    [property: JsonPropertyName("identity_ref")] string? IdentityRef = null,
    [property: JsonPropertyName("verification_level")] string? VerificationLevel = null)
{
    public EnrollWaitlistRequest() : this(
        string.Empty, default, default, 0, string.Empty, string.Empty, 0, string.Empty, null, null)
    { }
}

/// <summary>Deposit terms sub-object on the enroll request.</summary>
public sealed record DepositTermsDto(
    [property: JsonPropertyName("amount_minor")] long AmountMinor,
    [property: JsonPropertyName("currency")] string Currency)
{
    public DepositTermsDto() : this(0, string.Empty) { }
}

/// <summary>Auto-book grant sub-object on the enroll request (recorded consent, §20).</summary>
public sealed record AutoBookGrantDto(
    [property: JsonPropertyName("consent_evidence_id")] string ConsentEvidenceId,
    [property: JsonPropertyName("consented_at")] DateTimeOffset ConsentedAt,
    [property: JsonPropertyName("max_party_size")] int MaxPartySize,
    [property: JsonPropertyName("constraint_from")] DateTimeOffset ConstraintFrom,
    [property: JsonPropertyName("constraint_to")] DateTimeOffset ConstraintTo,
    [property: JsonPropertyName("max_deposit_amount_minor")] long MaxDepositAmountMinor,
    [property: JsonPropertyName("deposit_currency")] string DepositCurrency)
{
    public AutoBookGrantDto() : this(string.Empty, default, 0, default, default, 0, string.Empty) { }
}
