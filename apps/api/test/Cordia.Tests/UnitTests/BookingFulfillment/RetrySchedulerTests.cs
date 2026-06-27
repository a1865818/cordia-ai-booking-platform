using Cordia.Application.Handlers.BookingFulfillment;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.BookingFulfillment;

/// <summary>
/// Unit tests for <see cref="RetryScheduler"/>: verifies the durable retry scheduled_job shape
/// and that SetNotBefore is called with the correct stepId / nextEligibleAt (spec 06 §15.2).
/// </summary>
[Trait("Category", "Unit")]
public class RetrySchedulerTests
{
    private readonly Mock<IScheduledJobRepository> _jobs = new();
    private readonly Mock<IWorkflowStepRepository> _steps = new();

    private RetryScheduler CreateScheduler() => new(_jobs.Object, _steps.Object);

    [Fact]
    public async Task ScheduleAsync_InsertsScheduledJobWithCorrectShape()
    {
        // Arrange
        ScheduledJob? captured = null;
        _jobs.Setup(r => r.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduledJob, CancellationToken>((j, _) => captured = j)
            .Returns(Task.CompletedTask);
        _steps.Setup(r => r.SetNotBeforeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tenantId = "t-sched";
        var operationId = "op-sched";
        var stepId = "ws-sched";
        var nextEligibleAt = DateTimeOffset.UtcNow.AddSeconds(10);

        // Act
        await CreateScheduler().ScheduleAsync(tenantId, operationId, stepId, "finalize", nextEligibleAt, CancellationToken.None);

        // Assert: job shape
        Assert.NotNull(captured);
        Assert.Equal(tenantId, captured!.TenantId);
        Assert.Equal("booking.command.requested.v1", captured.JobType);
        Assert.Equal("booking.commands", captured.DestinationQueue);
        Assert.Equal(nextEligibleAt, captured.RunAfter);
        Assert.Equal(operationId, captured.OperationId);
        Assert.Equal(stepId, captured.WorkflowStepId);
        Assert.Equal(ScheduledJobStatus.Pending, captured.Status);
        Assert.Equal(0, captured.Attempt);
        Assert.StartsWith("sj_", captured.JobId);
    }

    [Fact]
    public async Task ScheduleAsync_SetsStepNotBefore_WithCorrectArguments()
    {
        // Arrange
        _jobs.Setup(r => r.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        DateTimeOffset? capturedNotBefore = null;
        string? capturedStepId = null;
        _steps.Setup(r => r.SetNotBeforeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, DateTimeOffset, CancellationToken>((_, sid, nb, _) =>
            {
                capturedStepId = sid;
                capturedNotBefore = nb;
            })
            .Returns(Task.CompletedTask);

        var stepId = "ws-nb-test";
        var nextEligibleAt = DateTimeOffset.UtcNow.AddSeconds(20);

        // Act
        await CreateScheduler().ScheduleAsync("t", "op", stepId, "finalize", nextEligibleAt, CancellationToken.None);

        // Assert: SetNotBefore called with correct args
        Assert.Equal(stepId, capturedStepId);
        Assert.Equal(nextEligibleAt, capturedNotBefore);
    }
}
