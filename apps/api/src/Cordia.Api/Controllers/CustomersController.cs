using Microsoft.Extensions.Options;
using System.Text.Json.Serialization;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Console.GetCustomer;
using Cordia.Application.Handlers.Console.ListCustomers;
using Cordia.Application.Handlers.Console.SetCustomerNotes;
using Cordia.Domain.Console;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Read-only customer directory for the operations console, plus operator notes.
///
/// GET  api/customers?query=&amp;limit= → <see cref="ListCustomersResponse"/> (200 | 400)
/// GET  api/customers/{ref} → <see cref="CustomerView"/> (200 | 404)
/// PUT  api/customers/{ref}/notes → <see cref="CustomerView"/> (200 | 403) operator + reason
///
/// There is no customers table (spec 12 owns all DDL and does not ratify one): this is an
/// aggregate over booking operations and their confirmed intent revisions, keyed by
/// <c>customer_ref</c>. It carries no NAME, and no aggregate beyond counts and timestamps.
///
/// <para><b>`customer_ref` is MINIMISED here, because the stored value is not reliably opaque.</b>
/// It is <c>bookings.customer_id</c>, which <c>FinalizeBookingCommandHandler</c> resolves through
/// <c>customer_id → contact_ref → caller_id → phone_e164 → email</c>. The console mints an opaque
/// <c>contact_ref</c> before it hashes a payload, but the agent lane does not — nothing in
/// <c>cordia_agent</c> populates <c>contact_ref</c>, and it carries <c>phone_e164</c> inline — so an
/// agent-created booking lands with a raw phone number in that column. This comment used to claim
/// the value was opaque; it was not, and this read, the schedule projection, the sessions list and
/// the waitlist list each returned guest contact details to any caller holding tenant scope, with
/// no grant, no reason code and no audit row — a lower bar than the transcript read of the same
/// data class.</para>
///
/// <para><c>ContactMinimisation.Mask</c> now runs at the point of emission on all four, so a phone
/// or an email leaves as <c>•••222</c> / <c>p•••@example.com</c> while an opaque handle passes
/// through untouched. The <c>?query=</c> filter matches the MASKED value, which removes the
/// phone-prefix oracle a raw substring match provided. Grouping still uses the raw key, so the
/// counts are unchanged.</para>
///
/// <para>The un-masked contact stays where it is actually gated: <c>GET /api/Bookings/{bookingId}</c>
/// behind a spec-08 grant. The remaining source-level fix — minting a <c>contact_ref</c> in the
/// agent lane so the column stops being an address at all — is still worth doing; this closes the
/// exposure at the read boundary rather than at its origin.</para>
/// </summary>
[ApiController]
[Route("api/customers")]
public class CustomersController(
    IMediator mediator,
    ITenantContext tenant,
    IActorContext actorContext,
    IOptions<AssertedActorOptions> assertedActor) : ControllerBase
{
    /// <summary>Default page size when the caller does not specify one.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Lists customers by most recent booking activity.</summary>
    /// <param name="query">Case-insensitive substring match on the customer reference.</param>
    /// <param name="limit">Maximum rows to return (1–200).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    [ProducesResponseType(typeof(ListCustomersResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ListCustomersResponse>> List(
        [FromQuery(Name = "query")] string? query,
        [FromQuery(Name = "limit")] int? limit,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        OperatorGuard.RequireOperator(
            actorContext, assertedActor.Value.AllowAssertedStaffRole, "customer.list_read");

        var result = await mediator.Send(
            new ListCustomersQuery(tenant.TenantId, query, limit ?? DefaultLimit),
            ct);

        return Ok(new ListCustomersResponse(
            result.Truncated,
            result.Customers.Select(ToView).ToList()));
    }

    /// <summary>
    /// One customer: booking history plus operator notes. Same minimisation as the list —
    /// never an unmasked phone or email.
    /// </summary>
    [HttpGet("{customerRef}")]
    [ProducesResponseType(typeof(CustomerView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomerView>> Get(string customerRef, CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        OperatorGuard.RequireOperator(
            actorContext, assertedActor.Value.AllowAssertedStaffRole, "customer.list_read");

        var customer = await mediator.Send(new GetCustomerQuery(tenant.TenantId, customerRef), ct);
        return customer is null ? NotFound() : Ok(ToView(customer));
    }

    /// <summary>Upserts operator notes and tags. Operator-gated; returns the same shape as GET.</summary>
    [HttpPut("{customerRef}/notes")]
    [ProducesResponseType(typeof(CustomerView), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<CustomerView>> PutNotes(
        string customerRef,
        [FromBody] SetCustomerNotesRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        // No OperatorGuard here: this is an IOperatorActionRequest, and OperatorActionBehavior makes
        // the same proven-identity decision AND writes the DENY audit (spec 14 §24.5). A controller
        // guard would refuse first and leave no audit row.
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        var customer = await mediator.Send(
            new SetCustomerNotesCommand(
                tenant.TenantId,
                customerRef,
                body?.Note,
                body?.Tags,
                resolvedActor,
                resolvedRole,
                reasonCode ?? string.Empty),
            ct);

        return Ok(ToView(customer));
    }

    private static CustomerView ToView(CustomerSummary c)
        => new(
            c.CustomerRef,
            c.BookingCount,
            c.FirstSeenAt,
            c.LastSeenAt,
            c.LastServiceId,
            c.LastBookingId,
            c.Origin,
            c.NoShowCount,
            c.Tags,
            c.Note);
}

public sealed record CustomerView(
    [property: JsonPropertyName("customer_ref")] string CustomerRef,
    [property: JsonPropertyName("booking_count")] int BookingCount,
    [property: JsonPropertyName("first_seen_at")] DateTimeOffset? FirstSeenAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("last_service_id")] string? LastServiceId,
    [property: JsonPropertyName("last_booking_id")] string? LastBookingId,
    [property: JsonPropertyName("origin")] string? Origin,
    [property: JsonPropertyName("no_show_count")] int NoShowCount,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("note")] string? Note);

public sealed record ListCustomersResponse(
    /// <summary>True when the search scanned its ceiling without exhausting the tenant.</summary>
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("customers")] IReadOnlyList<CustomerView> Customers);

public sealed record SetCustomerNotesRequest(
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("tags")] IReadOnlyList<string>? Tags);
