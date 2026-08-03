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
/// aggregate over booking operations and their confirmed intent revisions, keyed by the
/// opaque <c>customer_ref</c>. It exposes counts and timestamps only — no name, phone or
/// email (spec 14 minimisation, Inv-12). Contact details stay behind the
/// verification-gated <c>GET /api/Bookings/{bookingId}</c>.
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
