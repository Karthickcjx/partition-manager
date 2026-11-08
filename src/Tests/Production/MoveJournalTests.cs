using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;
using PartitionManager.Infrastructure.WindowsApi;
using PartitionManager.PrivilegedHelper.Engine;
using Xunit;
using static PartitionManager.Tests.Fixtures;
using static PartitionManager.Tests.Production.ExecutionFixtures;

namespace PartitionManager.Tests.Production;

/// <summary>
/// Move against the real file journal. Found in multi-agent review: a "volume in use" refusal left a Failed journal that
/// blocked every later real operation, and a large move outgrew the 16 MiB journal inspection limit.
/// </summary>
public sealed class MoveJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pm-movejournal-" + Guid.NewGuid().ToString("N"));
    private readonly List<EngineHarness> _harnesses = [];
    public MoveJournalTests() => Directory.CreateDirectory(_root);

    private (EngineHarness H, WriteEngine Engine, FileOperationJournal Journal) Setup(long partitionBytes)
    {
        var d = VirtualDisk();
        d = d with { SizeBytes = Math.Max(d.SizeBytes, 4 * partitionBytes) };
        d = d.WithPartitions(d.Partitions.Select(p => p with { SizeBytes = partitionBytes, VolumeSizeBytes = partitionBytes, FreeBytes = partitionBytes / 2 }));
        var h = new EngineHarness(d);
        _harnesses.Add(h);
        var journal = new FileOperationJournal(_root);
        var engine = new WriteEngine(h.Discovery, h.Backend, new FixedHelperPolicySource(h.Policy), h.Audit, h.Volumes) { Journal = journal, VerifyTimeout = TimeSpan.FromMilliseconds(200) };
        return (h, engine, journal);
    }

    private static WriteOperationRequest Move(DiskInfo d, PartitionInfo p, long dest) =>
        new(WriteOperationKind.MovePartition, DiskIdentity.From(d), PartitionTarget.From(p), null, null, null, OffsetBytes: dest);

    [Fact]
    public void Volume_in_use_refusal_does_not_block_the_next_operation()
    {
        var (h, engine, journal) = Setup(2 * GiB);
        using var _ = journal;
        var d = h.Discovery.State.Disk(3);
        var p = d.FindPartition(2)!;
        h.Backend.RelocateFailure = new IOException("The volume is in use (lock failed, error 5). Close every program using it, then retry. Nothing was changed.");
        Assert.Equal("MOVE_NOT_STARTED", engine.Execute(Guid.NewGuid(), Move(d, p, p.EndBytes + 64 * MiB)).Code);
        Assert.DoesNotContain(FileOperationJournal.Inspect(_root), j => j.RequiresReview);

        h.Backend.RelocateFailure = null;
        var next = engine.Execute(Guid.NewGuid(), LabelRequest(h.Discovery.State.Disk(3)));
        Assert.True(next.Status == HelperResponseStatus.Ok, $"{next.Code}: {next.Message}");
    }

    [Fact]
    public void Failure_after_writes_still_blocks_later_operations()
    {
        var (h, engine, journal) = Setup(2 * GiB);
        using var _ = journal;
        var d = h.Discovery.State.Disk(3);
        var p = d.FindPartition(2)!;
        h.Backend.RelocateFailure = new RelocationFailedException(new RelocationProgress { FirstWriteIssued = true }, new IOException("device error"));
        engine.Execute(Guid.NewGuid(), Move(d, p, p.EndBytes + 64 * MiB));
        Assert.Contains(FileOperationJournal.Inspect(_root), j => j.RequiresReview);
        h.Backend.RelocateFailure = null;
        Assert.Contains("RECOVERY_REVIEW_REQUIRED", engine.Execute(Guid.NewGuid(), LabelRequest(h.Discovery.State.Disk(3))).Message);
    }

    [Fact]
    public void Journal_of_a_large_move_stays_within_the_inspection_limit()
    {
        // 30 GiB moved by 1 MiB = 30,720 chunks.
        var (h, engine, journal) = Setup(30 * GiB);
        using var _ = journal;
        h.Backend.RelocateChunkBytes = MiB;
        var d = h.Discovery.State.Disk(3);
        var p = d.FindPartition(2)!;
        var r = engine.Execute(Guid.NewGuid(), Move(d, p, p.OffsetBytes + MiB));
        Assert.True(r.Status == HelperResponseStatus.Ok, $"{r.Code}: {r.Message}");
        var inspected = Assert.Single(FileOperationJournal.Inspect(_root));
        Assert.True(inspected.Valid, inspected.Problem);
        Assert.False(inspected.RequiresReview);
        Assert.True(new FileInfo(inspected.File).Length < 8L * 1024 * 1024, $"journal is {new FileInfo(inspected.File).Length} bytes");
        Assert.Contains(inspected.Events, e => e.Phase == "RelocationPlanned");
    }

    public void Dispose()
    {
        foreach (var h in _harnesses) h.Volumes.Dispose();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
