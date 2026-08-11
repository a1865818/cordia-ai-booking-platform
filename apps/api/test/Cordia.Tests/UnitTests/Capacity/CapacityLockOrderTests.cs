// The subject is a pure ordering primitive driven by mocks — no test can be hanging on a token.
#pragma warning disable xUnit1051
using Cordia.Domain.Interfaces;
using Cordia.Infrastructure.Services;
using Moq;

namespace Cordia.Tests.UnitTests.Capacity;

/// <summary>
/// <c>CapacityLockOrder</c> is the single implementation of the capacity lock order, so the ordering
/// property has to hold for ANY input — not just the orders the current call sites happen to pass.
///
/// <para>The real-PostgreSQL proofs live in
/// <c>IntegrationTests/Capacity/CapacityLockOrderTests</c>, which shows the wrong order genuinely
/// deadlocks (<c>40P01</c>) and this one does not. These tests cover the part that is cheap to get
/// wrong and invisible in a passing integration run: the normalisation.</para>
/// </summary>
[Trait("Category", "Unit")]
public class CapacityLockOrderUnitTests
{
    private readonly Mock<ICapacityHoldRepository> _holds = new();
    private readonly Mock<ICapacitySlotRepository> _slots = new();
    private readonly List<string> _calls = [];

    public CapacityLockOrderUnitTests()
    {
        _holds.Setup(h => h.LockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, id, _) => _calls.Add($"hold:{id}"))
            .Returns(Task.CompletedTask);

        _slots.Setup(s => s.LockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((_, id, _) => _calls.Add($"slot:{id}"))
            .Returns(Task.CompletedTask);
    }

    private CapacityLockOrder Subject => new(_holds.Object, _slots.Object);

    /// <summary>
    /// Clause 1: holds before slots. The original modify-vs-cancel deadlock was exactly this
    /// inversion, and the reason it is enforced in ONE place is that every call site that has to
    /// re-derive it is a call site that can get it wrong.
    /// </summary>
    [Fact]
    public async Task AllHoldsAreLockedBeforeAnySlot()
    {
        await Subject.AcquireAsync("t", holdIds: ["h2", "h1"], slotIds: ["s2", "s1"]);

        Assert.Equal(["hold:h1", "hold:h2", "slot:s1", "slot:s2"], _calls);
    }

    /// <summary>
    /// Clauses 2 and 3: each set ascending, whatever order the caller supplies.
    ///
    /// <para>This is the property that removes the cross-slot cycle. Two reschedules moving in
    /// opposite directions between the same pair name those slots in opposite orders — one sees
    /// <c>S1</c> as its old slot, the other as its new one — and if the acquisition preserved caller
    /// order they would take the two rows in opposite directions and deadlock.</para>
    /// </summary>
    [Theory]
    [InlineData("s1", "s2")]
    [InlineData("s2", "s1")]
    public async Task SlotsAreAcquiredAscending_WhateverOrderTheCallerSupplies(string first, string second)
    {
        await Subject.AcquireAsync("t", holdIds: [], slotIds: [first, second]);

        Assert.Equal(["slot:s1", "slot:s2"], _calls);
    }

    /// <summary>Same for holds — two amendments on adjacent operations must not invert on each other.</summary>
    [Theory]
    [InlineData("hold_a", "hold_b")]
    [InlineData("hold_b", "hold_a")]
    public async Task HoldsAreAcquiredAscending_WhateverOrderTheCallerSupplies(string first, string second)
    {
        await Subject.AcquireAsync("t", holdIds: [first, second], slotIds: []);

        Assert.Equal(["hold:hold_a", "hold:hold_b"], _calls);
    }

    /// <summary>
    /// Nulls, blanks and duplicates are absorbed. Call sites pass optional ids straight through
    /// (<c>[previousHold?.HoldId, ownHold?.HoldId]</c>) and a same-slot amendment resolves both slot
    /// ids to the same row, so this is the shape the callers actually rely on — requiring them to
    /// pre-filter would put a second piece of ordering-adjacent logic back at every call site.
    /// </summary>
    [Fact]
    public async Task NullsBlanksAndDuplicatesAreAbsorbed()
    {
        await Subject.AcquireAsync(
            "t",
            holdIds: [null, "h1", "", "h1", null],
            slotIds: ["s1", "s1", null]);

        Assert.Equal(["hold:h1", "slot:s1"], _calls);
    }

    /// <summary>
    /// The tenant travels with every lock (Inv-13). A lock statement without a tenant predicate
    /// would take a row belonging to someone else.
    /// </summary>
    [Fact]
    public async Task EveryLockIsTenantScoped()
    {
        await Subject.AcquireAsync("tenant-42", holdIds: ["h1"], slotIds: ["s1"]);

        _holds.Verify(h => h.LockAsync("tenant-42", "h1", It.IsAny<CancellationToken>()), Times.Once);
        _slots.Verify(s => s.LockAsync("tenant-42", "s1", It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Nothing to lock is not an error — a venue may declare no slots at all.</summary>
    [Fact]
    public async Task AcquiringNothingIsANoOp()
    {
        await Subject.AcquireAsync("t", holdIds: [], slotIds: []);

        Assert.Empty(_calls);
    }
}
