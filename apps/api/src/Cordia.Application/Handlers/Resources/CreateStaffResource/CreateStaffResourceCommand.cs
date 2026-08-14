using Cordia.Application.Common.Authorization;
using Cordia.Application.Handlers.Resources.ListStaffResources;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Resources.CreateStaffResource;

/// <summary>
/// Adds a named staff member the tenant can assign to work. Owner-gated — the roster is a
/// commercial setting, not front-desk work.
/// </summary>
public sealed record CreateStaffResourceCommand(
    string TenantId,
    string Name,
    string? Role,
    string Actor,
    string ActorRole,
    string ReasonCode) : IRequest<StaffResourceView>, IOperatorActionRequest
{
    string IOperatorActionRequest.ActorId => Actor;

    public string RequiredRole => StaffRole.Owner;

    public string ActionName => "staff_resource.created";

    public string TargetType => "staff_resource";

    public string TargetId => Name;
}

public sealed class CreateStaffResourceValidator : AbstractValidator<CreateStaffResourceCommand>
{
    public CreateStaffResourceValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        // Deliberately NO rule on Actor/ActorRole/ReasonCode — missing actor is authorization.
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200)
            .Must(n => !string.IsNullOrWhiteSpace(n))
            .WithMessage("A staff name is required.");
        RuleFor(x => x.Role)
            .MaximumLength(64)
            .When(x => x.Role is not null);
    }
}
