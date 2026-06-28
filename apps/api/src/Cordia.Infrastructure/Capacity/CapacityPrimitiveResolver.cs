using Cordia.Domain.Capacity;
using Cordia.Domain.Generated;

namespace Cordia.Infrastructure.Capacity;

/// <summary>
/// Default implementation of <see cref="ICapacityPrimitiveResolver"/> (spec 07 §3, §6.3).
/// <para>
/// Open-decision §13 default: every (tenant, provider, slot-source) triple resolves to
/// <see cref="CapacityPrimitive.FirstParty"/>. No real external provider is configured yet.
/// When a per-(tenant, provider) lookup table is introduced (future ticket), replace the
/// body of <see cref="Resolve"/> with a DB/config lookup and fall through to FirstParty when
/// no match is found.
/// </para>
/// </summary>
public sealed class CapacityPrimitiveResolver : ICapacityPrimitiveResolver
{
    /// <inheritdoc/>
    /// <remarks>
    /// Always returns <see cref="CapacityPrimitive.FirstParty"/> (spec 07 §13 open decision #1 and #2:
    /// first-party capacity_slots is the authority; no real external provider configured).
    /// The parameters are accepted for the future per-(tenant, provider) routing table.
    /// </remarks>
    public CapacityPrimitive Resolve(string tenantId, string providerName, string? slotSource)
        => CapacityPrimitive.FirstParty;

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
