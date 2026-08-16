using System.Text.Json.Serialization;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.DepositPolicy.GetDepositPolicy;
using Cordia.Application.Handlers.DepositPolicy.SetDepositPolicy;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Tenant deposit-policy settings row (docs/backend-gaps.md G7). Does not replace
/// <c>IDepositPolicy</c>.
///
/// GET  /api/deposit-policy → 200 | 400
/// PUT  /api/deposit-policy → 200 | 400 | 403
/// </summary>
[ApiController]
[Route("api/deposit-policy")]
public class DepositPolicyController(IMediator mediator, ITenantContext tenant, IActorContext actorContext) : ControllerBase
{
    /// <summary>
    /// Reads the deposit-policy row. Not owner-gated. Missing row → require false,
    /// amount_policy per_service, no_show_only false, refund_window "24 hours", currency AUD.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(DepositPolicyResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DepositPolicyResult>> Get(CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        return Ok(await mediator.Send(new GetDepositPolicyQuery(tenant.TenantId), ct));
    }

    /// <summary>Upserts the deposit-policy row. Owner-only and audited.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(DepositPolicyResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<DepositPolicyResult>> Put(
        [FromBody] SetDepositPolicyRequest? body,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        var result = await mediator.Send(
            new SetDepositPolicyCommand(
                tenant.TenantId,
                body?.Require ?? false,
                body?.AmountPolicy ?? string.Empty,
                body?.NoShowOnly ?? false,
                body?.RefundWindow ?? string.Empty,
                body?.AmountMinor,
                body?.Currency,
                resolvedActor,
                reasonCode ?? string.Empty,
                resolvedRole),
            ct);

        return Ok(result);
    }
}

/// <summary>Body for PUT /api/deposit-policy.</summary>
public sealed record SetDepositPolicyRequest(
    [property: JsonPropertyName("require")] bool Require,
    [property: JsonPropertyName("amount_policy")] string? AmountPolicy,
    [property: JsonPropertyName("no_show_only")] bool NoShowOnly,
    [property: JsonPropertyName("refund_window")] string? RefundWindow,
    [property: JsonPropertyName("amount_minor")] long? AmountMinor,
    [property: JsonPropertyName("currency")] string? Currency)
{
    public SetDepositPolicyRequest() : this(false, null, false, null, null, null) { }
}
