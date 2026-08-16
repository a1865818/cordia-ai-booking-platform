using System.Text.Json.Serialization;
using Cordia.Application.Common.Audit;
using Cordia.Application.Common.Authorization;
using Cordia.Domain.Bookings;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Mints an opaque handle for guest contact details, so the canonical payload can carry a
/// reference instead of the contact itself (spec 12/14 retention ruling, owner decision
/// 2026-08-06).
///
/// POST /api/guest-contacts → 200
///
/// <para><b>Why a separate call.</b> The canonical payload is hashed by the CLIENT before
/// submission, so contact can only be kept out of the hash if it never enters the payload the
/// client builds. The ref has to exist first. The alternative — letting the server strip contact
/// after the fact — makes the stored payload stop re-hashing to its own recorded hash, which is
/// the invariant break this whole change exists to avoid.</para>
///
/// <para>Idempotent per tenant + contact, so a guest who books repeatedly has ONE erasable row
/// rather than one per booking. That matters for erasure, not for storage: a guest asking to be
/// forgotten should not depend on the platform finding every copy.</para>
///
/// <para>Write-only by design. There is no GET: the ref is opaque to every client, and only the
/// notification resolver and discovery lookup dereference it, server-side. An endpoint that
/// turned a ref back into a phone number would re-create the exposure in a new place.</para>
///
/// <para><b>Audited, but deliberately not role-gated.</b> Minting a handle is a step in making a
/// booking, not a staff action, so requiring an operator role here would lock out the agent lane
/// that will mint refs for callers who never touch the console. What it must not be is silent:
/// this writes guest PII, so every mint appends an audit row naming the ref (never the contact) —
/// otherwise the one table the platform can erase from is also the one table nothing records
/// writes to. Who may call it is bounded by the tenant context, exactly as elsewhere; binding it
/// to an authenticated identity waits on signed actor context (spec 08/15), the same open work
/// that leaves <c>X-Actor</c> asserted rather than proven everywhere else.</para>
/// </summary>
[ApiController]
[Route("api/guest-contacts")]
public class GuestContactsController(
    IGuestContactRepository contacts,
    ITenantContext tenant,
    IAuditWriter audit,
    IUnitOfWork uow,
    IActorContext actorContext) : ControllerBase
{
    /// <summary>Returns the handle to put on the booking's canonical payload.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(GuestContactResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<GuestContactResponse>> Create(
        [FromBody] GuestContactRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);

        if (body is null)
            return Problem(statusCode: 400, title: "Missing body", detail: "A JSON body is required.");

        // A ref that dereferences to nothing is worse than no ref: the booking would carry a
        // handle implying a destination exists when none does, and Inv-16 forbids asserting what
        // was never recorded.
        //
        // Validated against the NORMALISED phone, not the raw string. `PhoneMatching.Normalise`
        // returns null for input that survives to nothing — punctuation only — and for anything
        // over its raw-length ceiling, so a request like "()-" or a very long formatted number
        // passed the non-blank check and minted a ref whose contact row held no number at all.
        // The caller got a 200 and a handle that could never be notified, which is precisely the
        // outcome the paragraph above forbids. A loud 400 is the honest answer.
        var normalisedPhone = PhoneMatching.Normalise(body.PhoneE164);

        // The email is held to the same standard as the phone: is it a DESTINATION?
        //
        // The blank check alone admitted `"."`, `"a"`, or any other non-whitespace string, which
        // then satisfied the "at least one usable contact" gate on its own and minted a ref whose
        // only channel can never be delivered to — the same dead handle the phone normalisation
        // exists to refuse, arrived at through the other field.
        var usableEmail = ContactAddress.NormaliseEmail(body.Email);

        if (normalisedPhone is null && usableEmail is null)
        {
            return Problem(
                statusCode: 400,
                title: "No usable contact supplied",
                detail: "At least one of phone_e164 (in international form, e.g. +61400111222) or "
                    + "email (a deliverable address, e.g. guest@example.com) is required — a name "
                    + "is not a destination, and a number the platform cannot normalise cannot be "
                    + "dialled.");
        }

        GuestContact contact = null!;
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        // ONE transaction for the PII write and the record of it.
        //
        // `GetOrCreateAsync` saves immediately, so with the audit as a separate write an audit
        // failure left guest contact stored with nothing recording that it had been — in the one
        // table the platform can erase from, whose whole justification is that every write to it
        // is accounted for. The caller meanwhile got an error saying nothing happened.
        await uow.ExecuteInTransactionAsync(async ct2 =>
        {
            contact = await contacts.GetOrCreateAsync(
                tenant.TenantId, body.PhoneE164, body.Email, body.DisplayName, ct2);

            // The ref, and WHICH channels were supplied — never the values. The point of the row is
            // that the contact is erasable; copying it into audit_events, which is never pruned,
            // would undo that in the one place retention cannot reach.
            await audit.WriteAsync(
                new AuditRecord(
                    TenantId: tenant.TenantId,
                    ActorType: StaffRole.IsKnown(resolvedRole) ? resolvedRole : "operator",
                    ActorId: string.IsNullOrWhiteSpace(resolvedActor) ? "unknown" : resolvedActor,
                    Action: "guest_contact.minted",
                    TargetType: "guest_contact",
                    TargetId: contact.ContactRef,
                    Decision: "allow",
                    DataClasses: ["contact"],
                    ReasonCode: string.IsNullOrWhiteSpace(reasonCode) ? null : reasonCode,
                    Extra: new
                    {
                        // The NORMALISED values, matching what `GetOrCreateAsync` actually stored.
                        // Keyed on the raw strings this claimed destinations existed for input that
                        // normalises to nothing, so the one immutable record of the write asserted
                        // destinations the contact row does not hold. Inv-16 forbids exactly that.
                        has_phone = normalisedPhone is not null,
                        has_email = usableEmail is not null,
                        has_display_name = !string.IsNullOrWhiteSpace(body.DisplayName),
                    }),
                ct2);
        }, ct);

        return Ok(new GuestContactResponse(contact.ContactRef));
    }
}

/// <summary>Body for POST /api/guest-contacts.</summary>
public sealed record GuestContactRequest(
    [property: JsonPropertyName("phone_e164")] string? PhoneE164,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("display_name")] string? DisplayName);

/// <summary>The opaque handle. Deliberately carries nothing else back.</summary>
public sealed record GuestContactResponse(
    [property: JsonPropertyName("contact_ref")] string ContactRef);
