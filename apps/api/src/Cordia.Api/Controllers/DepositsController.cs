using Microsoft.Extensions.Options;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Deposits.ListDeposits;
using Cordia.Application.Handlers.Deposits.RefundDeposit;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Deposit-session reads and operator refund (docs/backend-gaps.md G2).
///
/// GET  /api/deposits                 → 200 | 400
/// POST /api/deposits/{id}/refund     → 200 | 400 | 403 | 404 | 409
/// </summary>
[ApiController]
[Route("api/deposits")]
public class DepositsController(
    IMediator mediator,
    ITenantContext tenant,
    IActorContext actorContext,
    IOptions<AssertedActorOptions> assertedActor) : ControllerBase
{
    /// <summary>
    /// Lists recent deposit sessions. Filter <c>status</c> is the console vocabulary
    /// (held/captured/failed/refunded). Guest name is not invented.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(DepositListResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<DepositListResult>> List(
        [FromQuery] string? status,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        OperatorGuard.RequireOperator(
            actorContext, assertedActor.Value.AllowAssertedStaffRole, "deposit.list_read");
        return Ok(await mediator.Send(
            new ListDepositsQuery(
                tenant.TenantId,
                status,
                from,
                to,
                limit ?? ListDepositsValidator.DefaultLimit),
            ct));
    }

    /// <summary>
    /// Records a local Paid → Refunded transition. Operator + reason. No Stripe call.
    /// </summary>
    [HttpPost("{id}/refund")]
    [ProducesResponseType(typeof(RefundDepositResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RefundDepositResult>> Refund(
        string id,
        [FromHeader(Name = "X-Actor")] string? actor,
        [FromHeader(Name = "X-Reason-Code")] string? reasonCode,
        [FromHeader(Name = "X-Actor-Role")] string? actorRole,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        // No OperatorGuard here: this is an IOperatorActionRequest, and OperatorActionBehavior makes
        // the same proven-identity decision AND writes the DENY audit (spec 14 §24.5). A controller
        // guard would refuse first and leave no audit row.
        var (resolvedActor, resolvedRole) = ActorBinder.Resolve(actorContext, actor, actorRole);

        var result = await mediator.Send(
            new RefundDepositCommand(
                tenant.TenantId,
                id,
                resolvedActor,
                reasonCode ?? string.Empty,
                resolvedRole),
            ct);

        return result is null ? NotFound() : Ok(result);
    }
}
