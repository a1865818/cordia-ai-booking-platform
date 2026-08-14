using Cordia.Domain.Fulfillment;
using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Providers;

/// <summary>
/// Tenant-row backed <see cref="IDepositPolicy"/> (G2 / spec 09). Missing row or
/// <c>require: false</c> is the no-deposit path; <c>require: true</c> uses the stored
/// amount (default A$20.00) so booking-time actually creates a <c>DepositSession</c>.
/// </summary>
public sealed class TenantDepositPolicyProvider(ITenantDepositPolicyRepository repo) : IDepositPolicy
{
    public const int DefaultAmountMinor = 2000;
    public const string DefaultCurrency = "AUD";

    public async Task<DepositRequirement> GetRequirementAsync(
        string tenantId,
        string operationId,
        CancellationToken ct = default)
    {
        var row = await repo.GetAsync(tenantId, ct);
        if (row is null || !row.Require)
            return DepositRequirement.None;

        // no_show_only is persisted on the policy row but not applied here. Honouring it
        // would mean resolving this operation's customer and scanning prior bookings for
        // Attendance.NoShow — a multi-table join the booking-time seam does not have a
        // cheap repository for. Until that lookup exists, Require=true still requires a
        // deposit for every operation.
        var amount = row.AmountMinor is { } minor
            ? (int)Math.Clamp(minor, 0, int.MaxValue)
            : DefaultAmountMinor;
        var currency = string.IsNullOrWhiteSpace(row.Currency) ? DefaultCurrency : row.Currency;

        return new DepositRequirement(true, amount, currency);
    }
}
