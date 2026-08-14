using System.Text.Json.Serialization;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Auth.Login;

/// <summary>
/// Web-origin operator login (G1). Tenant is the OUTPUT — no <c>TenantGuard</c>.
/// Email-only lookup via <c>FindByEmailAcrossTenantsAsync</c> so the console can send
/// <c>{ email, password }</c> without naming a tenant.
/// </summary>
public sealed record LoginCommand(string Email, string Password) : IRequest<LoginResult?>;

/// <summary>Wire shape for a successful login (snake_case).</summary>
public sealed record LoginResult(
    [property: JsonPropertyName("tenant_context")] string TenantContext,
    [property: JsonPropertyName("actor_id")] string ActorId,
    [property: JsonPropertyName("actor_role")] string ActorRole,
    [property: JsonPropertyName("tenant_id")] string TenantId,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty();
        RuleFor(x => x.Password).NotEmpty();
    }
}
