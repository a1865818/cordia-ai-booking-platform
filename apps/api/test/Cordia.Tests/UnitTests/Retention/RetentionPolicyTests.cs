using Cordia.Domain.Enums;
using Cordia.Infrastructure.Services.Retention;

namespace Cordia.Tests.UnitTests.Retention;

/// <summary>
/// Unit tests for <see cref="RetentionPolicy"/> — pure policy logic, no DB.
/// Covers the four spec-prescribed branches (spec 12 §6.3, Inv-14).
/// </summary>
[Trait("Category", "Unit")]
public class RetentionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Past = Now.AddDays(-1);
    private static readonly DateTimeOffset Future = Now.AddDays(1);

    // (1) Past deadline + terminal status → should prune.
    [Fact]
    public void ShouldPrune_ReturnsTrueWhenPastDeadlineAndTerminalStatus()
        => Assert.True(RetentionPolicy.ShouldPrune(Past, Now, OperationStatus.Booked));

    // (2) Past deadline + non-terminal status (open work/dispute) → must NOT prune (Inv-14).
    [Fact]
    public void ShouldPrune_ReturnsFalseWhenOpenWorkWindow()
        => Assert.False(RetentionPolicy.ShouldPrune(Past, Now, OperationStatus.ManualReview));

    // (3) Future deadline + terminal status → not yet due, must NOT prune.
    [Fact]
    public void ShouldPrune_ReturnsFalseWhenDeadlineNotYetElapsed()
        => Assert.False(RetentionPolicy.ShouldPrune(Future, Now, OperationStatus.Booked));

    // (4) Past deadline + null linked status (orphan) → should prune.
    [Fact]
    public void ShouldPrune_ReturnsTrueWhenOrphanRecord()
        => Assert.True(RetentionPolicy.ShouldPrune(Past, Now, null));

    // (5) IsTerminal sanity: all four terminal statuses are terminal.
    [Theory]
    [InlineData(OperationStatus.Booked)]
    [InlineData(OperationStatus.Rejected)]
    [InlineData(OperationStatus.Expired)]
    [InlineData(OperationStatus.FailedFinal)]
    public void IsTerminal_ReturnsTrueForTerminalStatuses(OperationStatus status)
        => Assert.True(RetentionPolicy.IsTerminal(status));

    // (6) IsTerminal sanity: non-terminal statuses are not terminal.
    [Theory]
    [InlineData(OperationStatus.Accepted)]
    [InlineData(OperationStatus.ManualReview)]
    [InlineData(OperationStatus.Reconciling)]
    public void IsTerminal_ReturnsFalseForNonTerminalStatuses(OperationStatus status)
        => Assert.False(RetentionPolicy.IsTerminal(status));
}
