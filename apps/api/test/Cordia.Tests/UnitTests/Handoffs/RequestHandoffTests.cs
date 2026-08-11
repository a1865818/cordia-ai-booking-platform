using Cordia.Application.Handlers.Handoffs.RequestHandoff;
using Cordia.Domain.Entities;
using Cordia.Domain.Interfaces;

namespace Cordia.Tests.UnitTests.Handoffs;

/// <summary>
/// Unit tests for <c>request_human_handoff</c> (spec 03 §4.3).
///
/// The whole contract is idempotency: the agent derives the dedup key from
/// session + reason, so a retry, a reconnect, or two processes racing must all
/// end with ONE caller in the queue.
/// </summary>
[Trait("Category", "Unit")]
public class RequestHandoffTests
{
    private sealed class FakeRepository : IHandoffWorkItemRepository
    {
        private readonly List<HandoffWorkItem> _items = [];

        /// <summary>When set, the next TryAdd loses the race and this row wins.</summary>
        public HandoffWorkItem? RaceWinner { get; set; }

        public int AddAttempts { get; private set; }

        public Task<HandoffWorkItem?> GetByIdempotencyKeyAsync(
            string tenantId, string idempotencyKey, CancellationToken ct = default)
            => Task.FromResult(_items.FirstOrDefault(
                w => w.TenantId == tenantId && w.HandoffIdempotencyKey == idempotencyKey));

        public Task<bool> TryAddAsync(HandoffWorkItem workItem, CancellationToken ct = default)
        {
            AddAttempts++;
            if (RaceWinner is not null)
            {
                _items.Add(RaceWinner);
                RaceWinner = null;
                return Task.FromResult(false);
            }
            _items.Add(workItem);
            return Task.FromResult(true);
        }
    }

    private static RequestHandoffCommand Command(string key = "dedup-key-1") =>
        new("tenant-1", "sess-1", "verification_failed", key, RecommendedAction: null);

    [Fact]
    public async Task FirstCall_CreatesTheWorkItem()
    {
        var handler = new RequestHandoffCommandHandler(new FakeRepository());

        var result = await handler.Handle(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(RequestHandoffCommandHandler.StatusCreated, result.Status);
        Assert.StartsWith("hwi_", result.WorkItemId);
        Assert.Equal(RequestHandoffCommandHandler.DefaultQueue, result.Queue);
    }

    [Fact]
    public async Task SameKeyTwice_ReturnsTheSameWorkItemAsExisting()
    {
        var repository = new FakeRepository();
        var handler = new RequestHandoffCommandHandler(repository);

        var first = await handler.Handle(Command(), TestContext.Current.CancellationToken);
        var second = await handler.Handle(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(RequestHandoffCommandHandler.StatusExisting, second.Status);
        Assert.Equal(first.WorkItemId, second.WorkItemId);
        Assert.Equal(1, repository.AddAttempts);
    }

    [Fact]
    public async Task DifferentKeys_QueueSeparately()
    {
        var handler = new RequestHandoffCommandHandler(new FakeRepository());

        var first = await handler.Handle(Command("key-a"), TestContext.Current.CancellationToken);
        var second = await handler.Handle(Command("key-b"), TestContext.Current.CancellationToken);

        Assert.NotEqual(first.WorkItemId, second.WorkItemId);
        Assert.Equal(RequestHandoffCommandHandler.StatusCreated, second.Status);
    }

    [Fact]
    public async Task LosingTheInsertRace_ReturnsTheWinnersWorkItem()
    {
        // A reconnect escalating from a second process: the read found nothing,
        // then the unique index rejected the insert. Reporting our own id would
        // hand the agent a work item no human queue will ever show.
        var repository = new FakeRepository
        {
            RaceWinner = new HandoffWorkItem
            {
                TenantId = "tenant-1",
                WorkItemId = "hwi_winner",
                SessionId = "sess-1",
                ReasonCode = "verification_failed",
                HandoffIdempotencyKey = "dedup-key-1",
                Queue = RequestHandoffCommandHandler.DefaultQueue,
                State = RequestHandoffCommandHandler.StateOpen,
            },
        };
        var handler = new RequestHandoffCommandHandler(repository);

        var result = await handler.Handle(Command(), TestContext.Current.CancellationToken);

        Assert.Equal(RequestHandoffCommandHandler.StatusExisting, result.Status);
        Assert.Equal("hwi_winner", result.WorkItemId);
    }

    [Theory]
    [InlineData("", "sess", "reason", "key")]
    [InlineData("tenant", "", "reason", "key")]
    [InlineData("tenant", "sess", "", "key")]
    [InlineData("tenant", "sess", "reason", "")]
    public void MissingRequiredFields_FailValidation(
        string tenantId, string sessionId, string reasonCode, string key)
    {
        var validator = new RequestHandoffValidator();

        var result = validator.Validate(
            new RequestHandoffCommand(tenantId, sessionId, reasonCode, key, null));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AWellFormedRequest_PassesValidation()
    {
        Assert.True(new RequestHandoffValidator().Validate(Command()).IsValid);
    }
}
