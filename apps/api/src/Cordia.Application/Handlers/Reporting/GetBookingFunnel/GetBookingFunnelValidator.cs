using FluentValidation;

namespace Cordia.Application.Handlers.Reporting.GetBookingFunnel;

/// <summary>
/// Validates <see cref="GetBookingFunnelQuery"/> inputs (spec 13 §3):
/// <list type="bullet">
///   <item>TenantId must not be empty (Inv-13).</item>
///   <item>From must be earlier than To.</item>
///   <item>Window must not exceed 92 days to bound query cost.</item>
/// </list>
/// </summary>
public sealed class GetBookingFunnelValidator : AbstractValidator<GetBookingFunnelQuery>
{
    private static readonly TimeSpan MaxWindow = TimeSpan.FromDays(92);

    public GetBookingFunnelValidator()
    {
        RuleFor(x => x.TenantId)
            .NotEmpty()
            .WithMessage("TenantId is required.");

        RuleFor(x => x)
            .Must(q => q.From < q.To)
            .WithMessage("From must be earlier than To.");

        RuleFor(x => x)
            .Must(q => q.To - q.From <= MaxWindow)
            .WithMessage($"Window must not exceed {MaxWindow.TotalDays} days.");
    }
}
