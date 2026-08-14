using System.Text.Json.Serialization;
using Cordia.Domain.Interfaces;
using MediatR;

namespace Cordia.Application.Handlers.DepositPolicy.GetDepositPolicy;

/// <summary>
/// Reads the tenant deposit-policy row (docs/backend-gaps.md G7). Not owner-gated.
///
/// Missing row defaults: <c>require: false</c>, <c>amount_policy: per_service</c>,
/// <c>no_show_only: false</c>, <c>refund_window: "24 hours"</c>, <c>currency: "AUD"</c>.
/// This is the settings row only — it does not replace <c>IDepositPolicy</c>.
/// </summary>
public sealed record GetDepositPolicyQuery(string TenantId) : IRequest<DepositPolicyResult>;

public sealed record DepositPolicyResult(
    [property: JsonPropertyName("require")] bool Require,
    [property: JsonPropertyName("amount_policy")] string AmountPolicy,
    [property: JsonPropertyName("no_show_only")] bool NoShowOnly,
    [property: JsonPropertyName("refund_window")] string RefundWindow,
    [property: JsonPropertyName("amount_minor")] long? AmountMinor,
    [property: JsonPropertyName("currency")] string? Currency);

public static class DepositPolicyDefaults
{
    public const string AmountPolicyPerService = "per_service";
    public const string AmountPolicyFlat = "flat";
    public const string RefundWindow = "24 hours";
    public const string Currency = "AUD";

    public static readonly IReadOnlySet<string> AmountPolicies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AmountPolicyPerService, AmountPolicyFlat,
    };

    public static DepositPolicyResult Missing()
        => new(false, AmountPolicyPerService, false, RefundWindow, null, Currency);

    public static DepositPolicyResult From(Cordia.Domain.Entities.TenantDepositPolicy row)
        => new(
            row.Require,
            string.IsNullOrWhiteSpace(row.AmountPolicy) ? AmountPolicyPerService : row.AmountPolicy,
            row.NoShowOnly,
            string.IsNullOrWhiteSpace(row.RefundWindow) ? RefundWindow : row.RefundWindow,
            row.AmountMinor,
            string.IsNullOrWhiteSpace(row.Currency) ? Currency : row.Currency);
}

public sealed class GetDepositPolicyQueryHandler(ITenantDepositPolicyRepository repo)
    : IRequestHandler<GetDepositPolicyQuery, DepositPolicyResult>
{
    public async Task<DepositPolicyResult> Handle(GetDepositPolicyQuery request, CancellationToken ct)
    {
        var row = await repo.GetAsync(request.TenantId, ct);
        return row is null ? DepositPolicyDefaults.Missing() : DepositPolicyDefaults.From(row);
    }
}
