using Cordia.Domain.Generated;
using MediatR;

namespace Cordia.Application.Handlers.BookingOperations.SubmitConfirmedIntent;

/// <summary>
/// MediatR command that carries a validated <see cref="SubmitConfirmedIntentRequest"/> into the
/// acceptance handler (spec 04 §4.3, §10.3).
///
/// <para>The three authorization fields are transport concerns, not part of the ratified
/// <c>SubmitConfirmedIntentRequest</c> body, so they ride the same header shims every other
/// authorized path uses: <c>X-Grant-Id</c> (spec 08 §9.3) and <c>X-Actor</c>/<c>X-Actor-Role</c>
/// (spec 14 §24.5, GD-2). They are ignored for a create — only a mutation of an existing booking
/// needs authorizing (spec 08 §12: "every mutation accept requires a valid verification_grant or
/// authenticated owner role").</para>
/// </summary>
public record SubmitConfirmedIntentCommand(
    string TenantId,
    SubmitConfirmedIntentRequest Request,
    string? GrantId = null,
    string? ActorId = null,
    string? ActorRole = null) : IRequest<AcceptanceResult>;
