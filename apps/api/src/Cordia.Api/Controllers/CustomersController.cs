using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Console.ListCustomers;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Read-only customer directory for the operations console.
///
/// GET api/customers?query=&amp;limit= → <see cref="ListCustomersResponse"/> (200 | 400)
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
public class CustomersController(IMediator mediator, ITenantContext tenant) : ControllerBase
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

        var result = await mediator.Send(
            new ListCustomersQuery(tenant.TenantId, query, limit ?? DefaultLimit),
            ct);

        return Ok(new ListCustomersResponse(
            result.Truncated,
            result.Customers.Select(c => new CustomerView(
                c.CustomerRef,
                c.BookingCount,
                c.FirstSeenAt,
                c.LastSeenAt,
                c.LastServiceId,
                c.LastBookingId)).ToList()));
    }
}

public sealed record CustomerView(
    [property: JsonPropertyName("customer_ref")] string CustomerRef,
    [property: JsonPropertyName("booking_count")] int BookingCount,
    [property: JsonPropertyName("first_seen_at")] DateTimeOffset? FirstSeenAt,
    [property: JsonPropertyName("last_seen_at")] DateTimeOffset? LastSeenAt,
    [property: JsonPropertyName("last_service_id")] string? LastServiceId,
    [property: JsonPropertyName("last_booking_id")] string? LastBookingId);

public sealed record ListCustomersResponse(
    /// <summary>True when the search scanned its ceiling without exhausting the tenant.</summary>
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("customers")] IReadOnlyList<CustomerView> Customers);
