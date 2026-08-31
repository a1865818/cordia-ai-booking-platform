using Microsoft.Extensions.Options;
using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Payouts.GetPayoutSummary;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Payout snapshot (docs/backend-gaps.md G2). There is no Stripe Connect — next payout
/// is always null and the account label is "Not connected".
///
/// GET /api/payouts/summary → 200 | 400
/// </summary>
[ApiController]
[Route("api/payouts")]
public class PayoutsController(
    IMediator mediator,
    ITenantContext tenant,
    IActorContext actorContext,
    IOptions<AssertedActorOptions> assertedActor) : ControllerBase
{
    [HttpGet("summary")]
    [ProducesResponseType(typeof(PayoutSummaryResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PayoutSummaryResult>> Summary(CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        OperatorGuard.RequireOperator(
            actorContext, assertedActor.Value.AllowAssertedStaffRole, "payout.summary_read");
        return Ok(await mediator.Send(new GetPayoutSummaryQuery(tenant.TenantId), ct));
    }
}
