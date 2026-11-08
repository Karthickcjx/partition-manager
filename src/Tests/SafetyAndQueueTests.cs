using PartitionManager.Core.Logging;
using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;
using PartitionManager.Core.Safety;
using Xunit;
using static PartitionManager.Tests.Fixtures;

namespace PartitionManager.Tests;

public class SafetyTests
{
    private readonly SafetyPolicy _policy = new();

    [Theory]
    [InlineData(3)] // C:
    [InlineData(1)] // ESP
    [InlineData(4)] // Recovery
    public void System_partitions_cannot_be_deleted(int partition) =>
        Assert.False(_policy.Evaluate(new DeletePartitionOperation(0, partition), State(SystemDisk())).IsAllowed);

    [Fact]
    public void Boot_volume_cannot_be_formatted() =>
        Assert.False(_policy.Evaluate(new FormatPartitionOperation(0, 3, "NTFS", "", 0, true), State(SystemDisk())).IsAllowed);

    [Fact]
    public void System_disk_cannot_be_wiped() =>
        Assert.False(_policy.Evaluate(new DeleteAllPartitionsOperation(0), State(SystemDisk())).IsAllowed);

    [Fact]
    public void Boot_volume_can_be_extended_but_not_moved()
    {
        var s = State(SystemDisk());
        Assert.True(_policy.Evaluate(new ResizeMovePartitionOperation(0, 3, 117 * MiB, 199 * GiB), s).IsAllowed);
        Assert.False(_policy.Evaluate(new ResizeMovePartitionOperation(0, 3, 118 * MiB, 199 * GiB), s).IsAllowed);
    }

    [Fact]
    public void Boot_volume_letter_is_fixed() =>
        Assert.False(_policy.Evaluate(new ChangeDriveLetterOperation(0, 3, 'X'), State(SystemDisk())).IsAllowed);

    [Fact]
    public void Bitlocker_blocks_resize()
    {
        var d = DataDisk().WithPartitions([Part(1, 1, 1 * MiB, 10 * GiB, bitlocker: BitLockerState.Encrypted)]);
        var v = _policy.Evaluate(new ResizeMovePartitionOperation(1, 1, 1 * MiB, 20 * GiB), State(d));
        Assert.False(v.IsAllowed);
        Assert.Contains("BitLocker", v.Blockers[0]);
    }

    [Fact]
    public void Read_only_disk_blocks_everything()
    {
        var d = DataDisk() with { IsReadOnly = true };
        Assert.False(_policy.Evaluate(new ChangeLabelOperation(1, 1, "x"), State(d)).IsAllowed);
    }

    [Fact]
    public void Unhealthy_disk_blocks_high_risk()
    {
        var d = DataDisk() with { Health = HealthState.Unhealthy };
        Assert.False(_policy.Evaluate(new MergePartitionsOperation(1, 1, 2), State(d)).IsAllowed);
        Assert.True(_policy.Evaluate(new ChangeLabelOperation(1, 1, "x"), State(d)).IsAllowed);
    }

    [Fact]
    public void Destructive_operation_warns_about_data_loss()
    {
        var v = _policy.Evaluate(new DeletePartitionOperation(1, 1), State(DataDisk()));
        Assert.True(v.IsAllowed);
        Assert.Contains(v.Warnings, w => w.Contains("permanently erased"));
    }
}

public class QueueTests
{
    private static OperationQueue NewQueue() => new(State(DataDisk(), SystemDisk()), new SafetyPolicy());

    [Fact]
    public void Queue_builds_preview_and_leaves_baseline_alone()
    {
        var q = NewQueue();
        Assert.True(q.TryAdd(new DeletePartitionOperation(1, 2)).Success);
        Assert.Null(q.Preview.Disk(1).FindPartition(2));
        Assert.NotNull(q.Baseline.Disk(1).FindPartition(2));
    }

    [Fact]
    public void Blocked_operation_is_not_queued()
    {
        var q = NewQueue();
        var r = q.TryAdd(new DeletePartitionOperation(0, 3));
        Assert.False(r.Success);
        Assert.False(q.HasPending);
    }

    [Fact]
    public void Chained_operations_see_previous_results()
    {
        var q = NewQueue();
        Assert.True(q.TryAdd(new DeletePartitionOperation(1, 2)).Success);
        Assert.True(q.TryAdd(new ResizeMovePartitionOperation(1, 1, 1 * MiB, 40 * GiB)).Success);
        Assert.Equal(40 * GiB, q.Preview.Disk(1).FindPartition(1)!.SizeBytes);
    }

    [Fact]
    public void Removing_a_prerequisite_drops_dependent_operations()
    {
        var q = NewQueue();
        q.TryAdd(new DeletePartitionOperation(1, 2));
        q.TryAdd(new ResizeMovePartitionOperation(1, 1, 1 * MiB, 40 * GiB));
        var dropped = q.RemoveAt(0);
        Assert.Single(dropped);
        Assert.False(q.HasPending);
        Assert.Equal(10 * GiB, q.Preview.Disk(1).FindPartition(1)!.SizeBytes);
    }

    [Fact]
    public void Undo_and_clear()
    {
        var q = NewQueue();
        q.TryAdd(new ChangeLabelOperation(1, 1, "A"));
        q.TryAdd(new ChangeLabelOperation(1, 1, "B"));
        q.Undo();
        Assert.Equal("A", q.Preview.Disk(1).FindPartition(1)!.Label);
        q.Clear();
        Assert.Equal("Data1", q.Preview.Disk(1).FindPartition(1)!.Label);
    }

    [Fact]
    public void Rebase_drops_operations_for_disconnected_disk()
    {
        var q = NewQueue();
        q.TryAdd(new ChangeLabelOperation(1, 1, "A"));
        var dropped = q.Rebase(State(SystemDisk()));
        Assert.Single(dropped);
        Assert.False(q.HasPending);
    }

    [Fact]
    public void Changed_event_fires()
    {
        var q = NewQueue();
        var count = 0;
        q.Changed += (_, _) => count++;
        q.TryAdd(new ChangeLabelOperation(1, 1, "A"));
        q.Undo();
        Assert.Equal(2, count);
    }
}

public class ExecutorTests
{
    private static OperationQueue Queued()
    {
        var q = new OperationQueue(State(DataDisk()), new SafetyPolicy());
        q.TryAdd(new DeletePartitionOperation(1, 2));
        q.TryAdd(new ResizeMovePartitionOperation(1, 1, 1 * MiB, 40 * GiB));
        return q;
    }

    [Fact]
    public async Task Simulation_reaches_preview_state()
    {
        var q = Queued();
        var log = new MemoryOperationLog();
        var reports = new List<ExecutionProgress>();
        var exec = new SimulatedExecutor(log, TimeSpan.FromMilliseconds(1));
        var r = await exec.ExecuteAsync(q.Items, q.Baseline, new SyncProgress<ExecutionProgress>(reports.Add), CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Succeeded, r.Outcome);
        Assert.Equal(2, r.CompletedOperations);
        Assert.Equal(40 * GiB, r.Result.Disk(1).FindPartition(1)!.SizeBytes);
        Assert.Equal(100, reports[^1].Percent);
        Assert.True(reports.Select(p => p.Percent).SequenceEqual(reports.Select(p => p.Percent).Order()));
        Assert.All(log.Entries, e => Assert.Contains("[simulation]", e.Message));
    }

    [Fact]
    public async Task Cancellation_stops_between_steps_and_keeps_completed_work()
    {
        var q = Queued();
        using var cts = new CancellationTokenSource();
        var exec = new SimulatedExecutor(NullOperationLog.Instance, TimeSpan.FromMilliseconds(20));
        var progress = new SyncProgress<ExecutionProgress>(p => { if (p.OperationIndex == 1) cts.Cancel(); });
        var r = await exec.ExecuteAsync(q.Items, q.Baseline, progress, cts.Token);
        Assert.Equal(ExecutionOutcome.Cancelled, r.Outcome);
        Assert.Equal(1, r.CompletedOperations);
        Assert.Null(r.Result.Disk(1).FindPartition(2));
        Assert.Equal(10 * GiB, r.Result.Disk(1).FindPartition(1)!.SizeBytes);
    }

    [Fact]
    public async Task Failure_is_reported_with_partial_state()
    {
        var q = Queued();
        var exec = new SimulatedExecutor(NullOperationLog.Instance, TimeSpan.Zero) { FailAtOperation = 1 };
        var r = await exec.ExecuteAsync(q.Items, q.Baseline, null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Failed, r.Outcome);
        Assert.Equal(1, r.CompletedOperations);
        Assert.NotNull(r.Error);
    }

    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
