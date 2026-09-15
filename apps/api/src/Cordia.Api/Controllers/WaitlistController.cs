using Cordia.Domain.Enums;
using System.Text.Json.Serialization;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Console.ListWaitlistEnrollments;
using Cordia.Application.Handlers.Waitlist.Accept;
using Cordia.Application.Handlers.Waitlist.Cancel;
using Cordia.Application.Handlers.Waitlist.Decline;
using Cordia.Application.Handlers.Waitlist.Enroll;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using Cordia.Domain.Waitlist;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

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
/// illegal offer transition → 409).
///
/// Every mutation is an operator action (spec 14 §24.5, GD-1): it requires X-Actor, X-Actor-Role
/// (operator or owner) and X-Reason-Code, and `OperatorActionBehavior` deny-audits and 403s
/// without them. The reads are not gated — an operator needs the list to work from.
/// </summary>
[ApiController]
[Route("api/waitlist")]
public class WaitlistController(
    IMediator mediator,
    ITenantContext tenant,
    IActorContext actorContext,
    IOptions<AssertedActorOptions> assertedActor) : ControllerBase
{
    /// <summary>Default page size when the caller does not specify one.</summary>
    public const int DefaultListLimit = 100;

    /// <summary>
    /// The location ids this venue has already enrolled against.
    ///
    /// Not a registry — the platform has none, and inventing one is spec 12's call. It is the
    /// only un-invented source of truth available: the values actually in use. The matcher
    /// compares locations by exact string, so a typo yields an enrolment nothing can ever match
    /// and no error is raised anywhere (docs §12 gap 7); offering the real values is what stops
    /// that happening, and the console warns rather than blocks on an unrecognised one — a new
    /// venue legitimately has no locations yet.
    /// </summary>
    [HttpGet("locations")]
    [ProducesResponseType(typeof(WaitlistLocationsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<WaitlistLocationsResponse>> ListLocations(
        [FromServices] IWaitlistEnrollmentRepository enrollments,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        var locations = await enrollments.ListLocationsInUseAsync(tenant.TenantId, ct);
        return Ok(new WaitlistLocationsResponse(locations));
    }

    /// <summary>
    /// Lists waitlist enrollments with their latest offer state, newest first.
    ///
    /// <c>auto_book_authorized</c> reflects whether an explicit auto-book grant was recorded
    /// at enrollment; silent auto-book is prohibited by default (spec 11 §20, Inv-1), so the
    /// board shows this per row rather than implying a global toggle.
    /// </summary>
    [HttpGet("enrollments")]
    [ProducesResponseType(typeof(ListWaitlistEnrollmentsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ListWaitlistEnrollmentsResponse>> ListEnrollments(
        [FromQuery(Name = "active_only")] bool? activeOnly,
        [FromQuery(Name = "limit")] int? limit,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        OperatorGuard.RequireOperator(actorContext, assertedActor.Value.AllowAssertedStaffRole, "waitlist.enrollments_read");

        var enrollments = await mediator.Send(
            new ListWaitlistEnrollmentsQuery(tenant.TenantId, activeOnly ?? true, limit ?? DefaultListLimit),
            ct);

        return Ok(new ListWaitlistEnrollmentsResponse(
            enrollments.Select(e => new WaitlistEnrollmentView(
                e.EnrollmentId,
                e.CustomerRef,
                e.ServiceId,
                e.LocationId,
                e.PartySize,
                e.AcceptableFrom,
                e.AcceptableTo,
                e.AutoBookAuthorized,
                e.Cancelled,
                e.CreatedAt,
                e.LatestOfferId,
                EnumWireNames.ToWireName(e.LatestOfferStatus),
                e.LatestOfferExpiresAt)).ToList()));
    }

    /// <summary>Enrolls a customer on the waitlist. Returns 201 with the new enrollment id.</summary>
    [HttpPost("enrollments")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Enroll(
        [FromBody] EnrollWaitlistRequest body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        var deposit = body.DepositTerms is { } d ? new DepositTerms(d.AmountMinor, d.Currency) : null;
        var autoBook = body.AutoBook is { } a
            ? new AutoBookGrant(
                a.ConsentEvidenceId, a.ConsentedAt, a.MaxPartySize,
                a.ConstraintFrom, a.ConstraintTo, a.MaxDepositAmountMinor, a.DepositCurrency)
            : null;

        var enrollmentId = await mediator.Send(
            new EnrollWaitlistCommand(
                tenant.TenantId, resolvedActor, body.CustomerRef,
                body.AcceptableFrom, body.AcceptableTo, body.PartySize,
                body.LocationId, body.ServiceId, body.OfferLifetimeSeconds,
                deposit, body.NotificationChannel, autoBook,
                body.IdentityRef, body.VerificationLevel, body.IdempotencyKey,
                resolvedRole, reasonCode ?? string.Empty), ct);

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
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        await mediator.Send(new CancelWaitlistEnrollmentCommand(
            tenant.TenantId, enrollmentId, resolvedActor,
            resolvedRole, reasonCode ?? string.Empty), ct);
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
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        await mediator.Send(new AcceptWaitlistOfferCommand(
            tenant.TenantId, offerId, resolvedActor,
            resolvedRole, reasonCode ?? string.Empty), ct);
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
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        await mediator.Send(new DeclineWaitlistOfferCommand(
            tenant.TenantId, offerId, resolvedActor,
            resolvedRole, reasonCode ?? string.Empty), ct);
        return Ok(new { offer_id = offerId, status = "declined" });
    }

    // `ResolveActor` used to live here, turning a MISSING X-Actor into the literal "customer".
    // That fabricated an identity the request never established and wrote it into the audit
    // trail. The actor now flows through unchanged and OperatorActionBehavior refuses a blank one.
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
    [property: JsonPropertyName("verification_level")] string? VerificationLevel = null,
    /// <summary>
    /// Optional caller-minted idempotency key (§13.2). Supplying one is strictly better than
    /// relying on the server's derived fallback: the derived key is a hash of the enrolment's
    /// own details, so it cannot tell a retry apart from a deliberate second enrolment carrying
    /// the same details. A caller key is minted per attempt, so it says "this is the same
    /// attempt" rather than "these details look alike".
    /// </summary>
    [property: JsonPropertyName("idempotency_key")] string? IdempotencyKey = null)
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

/// <summary>One row of the console's waitlist board.</summary>
public sealed record WaitlistEnrollmentView(
    [property: JsonPropertyName("enrollment_id")] string EnrollmentId,
    [property: JsonPropertyName("customer_ref")] string CustomerRef,
    [property: JsonPropertyName("service_id")] string ServiceId,
    [property: JsonPropertyName("location_id")] string LocationId,
    [property: JsonPropertyName("party_size")] int PartySize,
    [property: JsonPropertyName("acceptable_from")] DateTimeOffset AcceptableFrom,
    [property: JsonPropertyName("acceptable_to")] DateTimeOffset AcceptableTo,
    [property: JsonPropertyName("auto_book_authorized")] bool AutoBookAuthorized,
    [property: JsonPropertyName("cancelled")] bool Cancelled,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("latest_offer_id")] string? LatestOfferId,
    [property: JsonPropertyName("latest_offer_status")] string? LatestOfferStatus,
    [property: JsonPropertyName("latest_offer_expires_at")] DateTimeOffset? LatestOfferExpiresAt);

public sealed record ListWaitlistEnrollmentsResponse(
    [property: JsonPropertyName("enrollments")] IReadOnlyList<WaitlistEnrollmentView> Enrollments);

/// <summary>
/// Locations this venue has enrolled against. A usage list, deliberately not called a registry —
/// the platform has no locations table and this does not pretend to be one.
/// </summary>
public sealed record WaitlistLocationsResponse(
    [property: JsonPropertyName("locations")] IReadOnlyList<string> Locations);
