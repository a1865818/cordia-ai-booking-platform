using System.Text.Json.Serialization;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using FluentValidation;
using MediatR;

namespace Cordia.Application.Handlers.Payouts.GetPayoutSummary;

/// <summary>
/// Honest payout snapshot (docs/backend-gaps.md G2). There is no Stripe Connect: held is
/// money still on bookings (Created + LinkSent), available is captured-not-paid-out (Paid).
/// Next payout date/amount are always null.
/// </summary>
public sealed record GetPayoutSummaryQuery(string TenantId) : IRequest<PayoutSummaryResult>;

public sealed record PayoutSummaryResult(
    [property: JsonPropertyName("held_minor")] long HeldMinor,
    [property: JsonPropertyName("available_minor")] long AvailableMinor,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("next_date")] string? NextDate,
    [property: JsonPropertyName("next_amount_minor")] long? NextAmountMinor,
    [property: JsonPropertyName("account")] string Account);

public sealed class GetPayoutSummaryValidator : AbstractValidator<GetPayoutSummaryQuery>
{
    public GetPayoutSummaryValidator() => RuleFor(x => x.TenantId).NotEmpty();
}

public sealed class GetPayoutSummaryQueryHandler(IDepositSessionRepository repo)
    : IRequestHandler<GetPayoutSummaryQuery, PayoutSummaryResult>
{
    // ListAsync is the only aggregate on the repository; a high window is enough for the
    // console snapshot. Without Connect there is no bank payout schedule to paginate past this.
    private const int SumWindow = 10_000;

    public async Task<PayoutSummaryResult> Handle(GetPayoutSummaryQuery request, CancellationToken ct)
    {
        var created = await repo.ListAsync(
            request.TenantId, DepositStatus.Created, null, null, SumWindow, ct);
        var linkSent = await repo.ListAsync(
            request.TenantId, DepositStatus.LinkSent, null, null, SumWindow, ct);
        var paid = await repo.ListAsync(
            request.TenantId, DepositStatus.Paid, null, null, SumWindow, ct);

        var heldMinor = created.Sum(s => (long)s.AmountMinor) + linkSent.Sum(s => (long)s.AmountMinor);
        var availableMinor = paid.Sum(s => (long)s.AmountMinor);
        var currency = created.Concat(linkSent).Concat(paid)
            .Select(s => s.Currency)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))
            ?? "AUD";

        return new PayoutSummaryResult(
            heldMinor,
            availableMinor,
            currency,
            NextDate: null,
            NextAmountMinor: null,
            Account: "Not connected");
    }
}
