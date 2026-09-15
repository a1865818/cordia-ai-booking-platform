using Cordia.Domain.Capacity;
using Cordia.Domain.Generated;
using Cordia.Domain.Interfaces;

namespace Cordia.Infrastructure.Services;

/// <summary>
/// Default implementation of <see cref="ICapacityPrimitiveResolver"/> (spec 07 §3, §6.3).
///
/// <para>Reads the rung from <c>tenant_capacity_primitives</c> (#106). It used to ignore all three
/// parameters and return <see cref="CapacityPrimitive.FirstParty"/>, so onboarding a provider with
/// native holds or atomic booking meant a code change.</para>
///
/// <para><b>Fallbacks, both to first-party.</b> No configured row is the spec 07 §13 default. And
/// the first-party provider is first-party by definition — its inventory is the conditional
/// <c>capacity_slots</c> update — so a row for it is ignored rather than trusted; the schema refuses
/// one too. <c>slotSource</c> is accepted for a future per-source override and not yet used.</para>
/// </summary>
public sealed class CapacityPrimitiveResolver(ITenantCapacityPrimitiveRepository configured) : ICapacityPrimitiveResolver
{
    /// <inheritdoc/>
    public async Task<CapacityPrimitive> ResolveAsync(
        string tenantId,
        string providerName,
        string? slotSource,
        CancellationToken ct = default)
    {
        if (string.Equals(providerName, CapacityProviders.FirstParty, StringComparison.Ordinal))
            return CapacityPrimitive.FirstParty;

        return await configured.GetAsync(tenantId, providerName, ct) ?? CapacityPrimitive.FirstParty;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Maps each primitive rung to the availability mode a successful reservation on that rung
    /// produces (spec 07 §5, mode→allowed-agent-claims table):
    /// <list type="bullet">
    ///   <item><see cref="CapacityPrimitive.FirstParty"/> → <see cref="AvailabilityModeEnum.Held"/> (durable conditional write)</item>
    ///   <item><see cref="CapacityPrimitive.NativeHold"/> → <see cref="AvailabilityModeEnum.Held"/> (provider expiring hold)</item>
    ///   <item><see cref="CapacityPrimitive.IdempotentPending"/> → <see cref="AvailabilityModeEnum.Held"/> (pending acts as hold, §6.3)</item>
    ///   <item><see cref="CapacityPrimitive.AtomicBook"/> → <see cref="AvailabilityModeEnum.Confirmed"/> (clean atomic book, §6.3)</item>
    ///   <item><see cref="CapacityPrimitive.CheckThenCreate"/> → <see cref="AvailabilityModeEnum.Live"/> (no hold; can go stale, §5)</item>
    /// </list>
    /// </remarks>
    public AvailabilityModeEnum ModeFor(CapacityPrimitive primitive) => primitive switch
    {
        CapacityPrimitive.FirstParty => AvailabilityModeEnum.Held,
        CapacityPrimitive.NativeHold => AvailabilityModeEnum.Held,
        CapacityPrimitive.IdempotentPending => AvailabilityModeEnum.Held,
        CapacityPrimitive.AtomicBook => AvailabilityModeEnum.Confirmed,
        CapacityPrimitive.CheckThenCreate => AvailabilityModeEnum.Live,
        _ => throw new ArgumentOutOfRangeException(nameof(primitive), primitive, "Unknown CapacityPrimitive rung."),
    };
}
