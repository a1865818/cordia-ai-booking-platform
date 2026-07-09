using System.Text.Json;
using Cordia.Application.Handlers.Preorder;
using Cordia.Domain.Entities;
using Cordia.Domain.Enums;
using Cordia.Domain.Interfaces;
using Moq;

namespace Cordia.Tests.UnitTests.Preorder;

/// <summary>
/// Spec 11 §5.3 (divergence #3) — per-step reconcile timeout scheduling. On entry to an in-flight saga
/// step the handler writes ONE <c>preorder.commands.reconcile.v1</c> scheduled job on the
/// <c>preorder.commands</c> lane, due after the step's window, with a deterministic job id so a
/// duplicate step entry / re-drive does not stack a second timer.
/// </summary>
[Trait("Category", "Unit")]
public class PreorderStepTimeoutTests
{
    private readonly Mock<IScheduledJobRepository> _jobs = new();
    private readonly List<ScheduledJob> _added = new();

    public PreorderStepTimeoutTests()
    {
        _jobs.Setup(r => r.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()))
            .Callback<ScheduledJob, CancellationToken>((j, _) => _added.Add(j))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task Schedules_Reconcile_Job_On_Preorder_Lane_With_Deterministic_Id_And_Window()
    {
        _jobs.Setup(r => r.ExistsAsync("t_1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var now = DateTimeOffset.UtcNow;

        await PreorderSagaSupport.ScheduleStepTimeoutAsync(
            _jobs.Object, "t_1", "pos_1", "op_1", PreorderSagaState.CommittingBooking, now, CancellationToken.None);

        var job = Assert.Single(_added);
        Assert.Equal("sched_preorder_pos_1_committingbooking", job.JobId);
        Assert.Equal(PreorderSagaMessages.Reconcile, job.JobType);
        Assert.Equal(PreorderSagaMessages.QueueWireName, job.DestinationQueue);
        Assert.Equal(ScheduledJobStatus.Pending, job.Status);
        Assert.Equal("op_1", job.OperationId);
        Assert.Equal(now + PreorderSagaSupport.TimeoutWindow(PreorderSagaState.CommittingBooking), job.RunAfter);

        using var doc = JsonDocument.Parse(job.Payload);
        Assert.Equal("pos_1", doc.RootElement.GetProperty("saga_id").GetString());
        Assert.Equal("op_1", doc.RootElement.GetProperty("operation_id").GetString());
    }

    [Fact]
    public async Task Duplicate_Step_Entry_Does_Not_Stack_A_Second_Timer()
    {
        // A pre-existing job (any status) short-circuits the insert — deterministic dedup.
        _jobs.Setup(r => r.ExistsAsync("t_1", "sched_preorder_pos_1_reservingitems", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await PreorderSagaSupport.ScheduleStepTimeoutAsync(
            _jobs.Object, "t_1", "pos_1", "op_1", PreorderSagaState.ReservingItems, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Empty(_added);
        _jobs.Verify(r => r.AddAsync(It.IsAny<ScheduledJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void CommittingBooking_Window_Is_Longest_Because_It_Waits_On_The_06_Engine()
    {
        // The booking wait spans capacity/payment/create, so its timeout must exceed the local steps.
        Assert.True(
            PreorderSagaSupport.TimeoutWindow(PreorderSagaState.CommittingBooking) >
            PreorderSagaSupport.TimeoutWindow(PreorderSagaState.ReservingItems));
        Assert.True(
            PreorderSagaSupport.TimeoutWindow(PreorderSagaState.CommittingBooking) >
            PreorderSagaSupport.TimeoutWindow(PreorderSagaState.CommittingPreorder));
    }
}
