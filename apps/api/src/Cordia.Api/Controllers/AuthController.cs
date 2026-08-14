using System.Text.Json.Serialization;
using Cordia.Application.Handlers.Auth.GetSession;
using Cordia.Application.Handlers.Auth.Login;
using Cordia.Application.Handlers.Auth.Logout;
using Cordia.Api.Middleware;
using Cordia.Domain.Interfaces;
using Cordia.Domain.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Cordia.Api.Controllers;

/// <summary>
/// Web-origin operator authentication (G1). Login mints an <c>opctx+jws</c> carried by the client
/// as <c>X-Tenant-Context</c> — no cookies, no <c>[Authorize]</c>.
///
/// <para>POST /api/auth/login — no tenant precondition (tenant is the OUTPUT).</para>
/// <para>GET  /api/auth/session + X-Tenant-Context — live session.</para>
/// <para>POST /api/auth/logout + X-Tenant-Context — revoke; 204 even if already revoked.</para>
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(IMediator mediator, ITenantContext tenant) : ControllerBase
{
    /// <summary>Authenticates an operator and returns a signed operator-session JWS.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LoginResult>> Login(
        [FromBody] LoginRequest body,
        CancellationToken ct)
    {
        var result = await mediator.Send(
            new LoginCommand(body.Email ?? string.Empty, body.Password ?? string.Empty),
            ct);

        if (result is null)
        {
            return Unauthorized(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Invalid credentials",
                Detail = "Invalid credentials",
            });
        }

        return Ok(result);
    }

    /// <summary>Returns the live operator session for a verified <c>opctx+jws</c>.</summary>
    [HttpGet("session")]
    [ProducesResponseType(typeof(SessionResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SessionResult>> GetSession(
        [FromHeader(Name = TenantContextVerificationMiddleware.HeaderName)] string? tenantContext,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        return Ok(await mediator.Send(
            new GetSessionQuery(tenant.TenantId, tenantContext ?? string.Empty), ct));
    }

    /// <summary>Revokes the operator session. 204 even if already revoked.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout(
        [FromHeader(Name = TenantContextVerificationMiddleware.HeaderName)] string? tenantContext,
        CancellationToken ct)
    {
        TenantGuard.RequireTenant(tenant);
        await mediator.Send(
            new LogoutCommand(tenant.TenantId, tenantContext ?? string.Empty), ct);
        return NoContent();
    }
}

/// <summary>POST /api/auth/login body.</summary>
public sealed record LoginRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("password")] string? Password)
{
    public LoginRequest() : this(null, null) { }
}
