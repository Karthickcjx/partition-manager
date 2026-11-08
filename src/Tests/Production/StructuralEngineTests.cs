using PartitionManager.Core.Execution;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;
using PartitionManager.Core.Safety;
using PartitionManager.Infrastructure.Ipc;
using Xunit;
using static PartitionManager.Tests.Fixtures;
using static PartitionManager.Tests.Production.ExecutionFixtures;

namespace PartitionManager.Tests.Production;

/// <summary>
/// Create / format / delete / shrink / extend / split through the real WriteEngine with a fake storage backend.
/// The virtual disk: 8 GiB GPT, partition 2 = 4 GiB NTFS at 16 MiB (Q:, 1 GiB used), free space after it.
/// </summary>
public class StructuralEngineTests
{
    private static readonly long FreeStart = 16 * MiB + 4 * GiB;

    private static HelperResponse Run(EngineHarness h, WriteOperationRequest r) => h.Engine.Execute(Guid.NewGuid(), r);
    private static DiskInfo Disk(EngineHarness h) => h.Discovery.State.Disk(3);
    private static PartitionInfo P2(EngineHarness h) => Disk(h).FindPartition(2)!;

    private static WriteOperationRequest Req(DiskInfo d, WriteOperationKind kind, long? size = null, string? fs = null, string? label = null, char? letter = null, long? offset = null, PartitionInfo? part = null) =>
        new(kind, DiskIdentity.From(d), kind == WriteOperationKind.CreatePartition ? null : PartitionTarget.From(part ?? d.FindPartition(2)!),
            label, letter, null, OffsetBytes: offset, SizeBytes: size, FileSystem: fs);

    // ---- create

    [Fact]
    public void Create_formats_labels_and_letters_the_new_partition()
    {
        var h = new EngineHarness(VirtualDisk());
        var r = Run(h, Req(Disk(h), WriteOperationKind.CreatePartition, size: GiB, fs: "NTFS", label: "NEW", letter: 'S', offset: FreeStart));
        Assert.Equal(HelperResponseStatus.Ok, r.Status);
        Assert.Equal([$"create 3 {FreeStart}+{GiB}", "format 3/3 NTFS", "add 3/3 S"], h.Backend.Calls);
        var created = Disk(h).FindPartition(3)!;
        Assert.Equal(("NTFS", "NEW", 'S'), (created.FileSystem, created.Label, created.DriveLetter!.Value));
        Assert.Contains("Other partitions unchanged", r.Steps);
    }

    [Fact]
    public void Create_over_an_existing_partition_is_rejected_without_writing()
    {
        var h = new EngineHarness(VirtualDisk());
        var r = Run(h, Req(Disk(h), WriteOperationKind.CreatePartition, size: GiB, fs: "NTFS", offset: 1 * GiB));
        Assert.Equal("NOT_ENOUGH_SPACE", r.Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Theory]
    [InlineData(4096L * 1024 * 1024 + 16 * 1024 * 1024 + 512, 1L << 30, "BAD_OFFSET")]   // unaligned offset
    [InlineData(0L, 1L << 30, "BAD_OFFSET")]                                             // inside the first MiB
    [InlineData(4096L * 1024 * 1024 + 16 * 1024 * 1024, 100L << 30, "BAD_SIZE")]          // larger than the disk
    [InlineData(4096L * 1024 * 1024 + 16 * 1024 * 1024, 4096L, "BAD_SIZE")]               // smaller than 1 MiB
    public void Invalid_create_geometry_is_rejected(long offset, long size, string code)
    {
        var h = new EngineHarness(VirtualDisk());
        Assert.Equal(code, Run(h, Req(Disk(h), WriteOperationKind.CreatePartition, size: size, fs: "NTFS", offset: offset)).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Create_beyond_the_free_space_is_rejected()
    {
        var h = new EngineHarness(VirtualDisk());
        var free = DiskLayout.FreeSegments(Disk(h)).Last(); // the large free region after partition 2
        var r = Run(h, Req(Disk(h), WriteOperationKind.CreatePartition, size: DiskLayout.AlignDown(free.SizeBytes) + MiB, fs: "NTFS", offset: free.OffsetBytes));
        Assert.Contains(r.Code, new[] { "NOT_ENOUGH_SPACE", "BAD_SIZE" });
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Create_that_fails_at_format_reports_the_raw_partition()
    {
        var h = new EngineHarness(VirtualDisk());
        h.Backend.FailOn = "format";
        var r = Run(h, Req(Disk(h), WriteOperationKind.CreatePartition, size: GiB, fs: "NTFS", offset: FreeStart));
        Assert.Equal(HelperResponseStatus.Failed, r.Status);
        Assert.Contains("Format new partition failed", r.Message);
        Assert.Contains("RAW", r.Message); // current layout is reported
    }

    // ---- format

    [Fact]
    public void Format_changes_file_system_and_label()
    {
        var h = new EngineHarness(VirtualDisk());
        var r = Run(h, Req(Disk(h), WriteOperationKind.FormatVolume, fs: "exFAT", label: "FMT"));
        Assert.Equal(HelperResponseStatus.Ok, r.Status);
        Assert.Equal(("exFAT", "FMT"), (P2(h).FileSystem, P2(h).Label));
    }

    [Fact]
    public void Unsupported_file_system_is_rejected()
    {
        var h = new EngineHarness(VirtualDisk());
        Assert.Equal("BAD_FILE_SYSTEM", Run(h, Req(Disk(h), WriteOperationKind.FormatVolume, fs: "ext4")).Code);
        Assert.Equal("BAD_FILE_SYSTEM", Run(h, Req(Disk(h), WriteOperationKind.FormatVolume, fs: null)).Code);
    }

    // ---- delete

    [Fact]
    public void Delete_removes_only_the_target()
    {
        var h = new EngineHarness(VirtualDisk());
        Assert.Equal(HelperResponseStatus.Ok, Run(h, Req(Disk(h), WriteOperationKind.CreatePartition, size: GiB, fs: "NTFS", offset: FreeStart)).Status);
        var r = Run(h, Req(Disk(h), WriteOperationKind.DeletePartition, part: Disk(h).FindPartition(3)));
        Assert.Equal(HelperResponseStatus.Ok, r.Status);
        Assert.Null(Disk(h).FindPartition(3));
        Assert.NotNull(P2(h));
    }

    [Fact]
    public void Delete_of_a_boot_partition_is_blocked_by_safety_policy()
    {
        var d = VirtualDisk();
        d = d.WithPartitions(d.Partitions.Select(p => p with { IsBoot = true }));
        var h = new EngineHarness(d);
        Assert.Equal("SAFETY_POLICY", Run(h, Req(Disk(h), WriteOperationKind.DeletePartition)).Code);
        Assert.Empty(h.Backend.Calls);
    }

    // ---- shrink / extend

    [Fact]
    public void Shrink_and_extend_within_supported_range()
    {
        var h = new EngineHarness(VirtualDisk());
        Assert.Equal(HelperResponseStatus.Ok, Run(h, Req(Disk(h), WriteOperationKind.ShrinkPartition, size: 2 * GiB)).Status);
        Assert.Equal(2 * GiB, P2(h).SizeBytes);
        Assert.Equal(HelperResponseStatus.Ok, Run(h, Req(Disk(h), WriteOperationKind.ExtendPartition, size: 5 * GiB)).Status);
        Assert.Equal(5 * GiB, P2(h).SizeBytes);
    }

    [Fact]
    public void Shrink_below_windows_minimum_is_rejected()
    {
        var h = new EngineHarness(VirtualDisk());
        h.Backend.SupportedSize = p => (3 * GiB, p.SizeBytes); // e.g. unmovable files near the end
        var r = Run(h, Req(Disk(h), WriteOperationKind.ShrinkPartition, size: 2 * GiB));
        Assert.Equal("BELOW_MINIMUM", r.Code);
        Assert.DoesNotContain(h.Backend.Calls, c => c.StartsWith("resize"));
    }

    [Fact]
    public void Extend_beyond_free_space_is_rejected()
    {
        var h = new EngineHarness(VirtualDisk());
        // Just past the end of the free space (8 GiB disk minus the backup GPT), but still inside the disk.
        Assert.Equal("NOT_ENOUGH_SPACE", Run(h, Req(Disk(h), WriteOperationKind.ExtendPartition, size: 8 * GiB - 16 * MiB)).Code);
    }

    [Theory]
    [InlineData(WriteOperationKind.ShrinkPartition, 2L * 1024 * 1024 * 1024 + 4096)]   // not whole MiB
    [InlineData(WriteOperationKind.ShrinkPartition, 5L * 1024 * 1024 * 1024)]          // "shrink" that grows
    [InlineData(WriteOperationKind.ExtendPartition, 1L * 1024 * 1024 * 1024)]          // "extend" that shrinks
    [InlineData(WriteOperationKind.ShrinkPartition, -1L)]
    public void Invalid_resize_sizes_are_rejected(WriteOperationKind kind, long size)
    {
        var h = new EngineHarness(VirtualDisk());
        Assert.Contains(Run(h, Req(Disk(h), kind, size: size)).Code, new[] { "BAD_SIZE", "MISSING_SIZE" });
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Shrinking_fat32_is_rejected_as_unsupported()
    {
        var d = VirtualDisk();
        d = d.WithPartitions(d.Partitions.Select(p => p with { FileSystem = "FAT32" }));
        var h = new EngineHarness(d);
        Assert.Equal("UNSUPPORTED_FILE_SYSTEM", Run(h, Req(Disk(h), WriteOperationKind.ShrinkPartition, size: 2 * GiB)).Code);
        Assert.Equal("UNSUPPORTED_FILE_SYSTEM", Run(h, Req(Disk(h), WriteOperationKind.SplitPartition, size: 2 * GiB, fs: "NTFS")).Code);
    }

    // ---- split

    [Fact]
    public void Split_shrinks_creates_formats_and_letters_in_the_freed_space()
    {
        var h = new EngineHarness(VirtualDisk());
        var before = P2(h);
        var r = Run(h, Req(Disk(h), WriteOperationKind.SplitPartition, size: 2 * GiB, fs: "NTFS", label: "SECOND", letter: 'T'));
        Assert.Equal(HelperResponseStatus.Ok, r.Status);
        var first = P2(h);
        var second = Disk(h).FindPartition(3)!;
        Assert.Equal(2 * GiB, first.SizeBytes);
        Assert.Equal(before.UniqueGuid, first.UniqueGuid);
        Assert.Equal(before.OffsetBytes + 2 * GiB, second.OffsetBytes);
        Assert.Equal(before.EndBytes, second.EndBytes);
        Assert.Equal(("NTFS", "SECOND", 'T'), (second.FileSystem, second.Label, second.DriveLetter!.Value));
        Assert.Equal(["supported 3/2", $"resize 3/2 {2 * GiB}", $"create 3 {before.OffsetBytes + 2 * GiB}+{2 * GiB}", "format 3/3 NTFS", "add 3/3 T"], h.Backend.Calls);
    }

    [Fact]
    public void Split_failing_at_shrink_changes_nothing()
    {
        var h = new EngineHarness(VirtualDisk());
        h.Backend.FailOn = "resize";
        var r = Run(h, Req(Disk(h), WriteOperationKind.SplitPartition, size: 2 * GiB, fs: "NTFS"));
        Assert.Equal("SPLIT_SHRINK_FAILED", r.Code);
        Assert.Equal(4 * GiB, P2(h).SizeBytes);
        Assert.Single(Disk(h).Partitions);
    }

    [Fact]
    public void Split_failing_at_create_reports_shrunk_partition_and_recovery()
    {
        var h = new EngineHarness(VirtualDisk());
        h.Backend.FailOn = "create";
        var r = Run(h, Req(Disk(h), WriteOperationKind.SplitPartition, size: 2 * GiB, fs: "NTFS"));
        Assert.Equal("SPLIT_CREATE_FAILED", r.Code);
        Assert.Equal(2 * GiB, P2(h).SizeBytes);                  // actual state: shrunk...
        Assert.Single(Disk(h).Partitions);                       // ...and no new partition
        Assert.Contains("extend the original partition back", r.Message);
        Assert.Contains("Current layout", r.Message);
        Assert.Contains(h.Audit.Entries, e => e.Outcome == "FAILED SPLIT_CREATE_FAILED");
    }

    [Fact]
    public void Split_failing_at_format_leaves_a_raw_partition_and_says_so()
    {
        var h = new EngineHarness(VirtualDisk());
        h.Backend.FailOn = "format";
        var r = Run(h, Req(Disk(h), WriteOperationKind.SplitPartition, size: 2 * GiB, fs: "NTFS"));
        Assert.Equal("SPLIT_FORMAT_FAILED", r.Code);
        Assert.Equal("", Disk(h).FindPartition(3)!.FileSystem);
        Assert.Contains("not formatted (RAW)", r.Message);
    }

    [Fact]
    public void Split_failing_at_letter_reports_formatted_partition_without_letter()
    {
        var h = new EngineHarness(VirtualDisk());
        h.Backend.FailOn = "add";
        var r = Run(h, Req(Disk(h), WriteOperationKind.SplitPartition, size: 2 * GiB, fs: "NTFS", letter: 'T'));
        Assert.Equal("SPLIT_LETTER_FAILED", r.Code);
        Assert.Equal("NTFS", Disk(h).FindPartition(3)!.FileSystem);
    }

    [Fact]
    public void Split_whose_shrink_does_not_take_effect_stops_before_creating()
    {
        var h = new EngineHarness(VirtualDisk());
        h.Backend.Silent = true;
        var r = Run(h, Req(Disk(h), WriteOperationKind.SplitPartition, size: 2 * GiB, fs: "NTFS"));
        Assert.Equal("SPLIT_SHRINK_UNVERIFIED", r.Code);
        Assert.DoesNotContain(h.Backend.Calls, c => c.StartsWith("create"));
    }

    [Fact]
    public void Split_below_minimum_is_rejected_before_any_write()
    {
        var h = new EngineHarness(VirtualDisk());
        Assert.Equal("BELOW_MINIMUM", Run(h, Req(Disk(h), WriteOperationKind.SplitPartition, size: 512 * MiB, fs: "NTFS")).Code);
        Assert.DoesNotContain(h.Backend.Calls, c => c.StartsWith("resize"));
    }

    [Fact]
    public void Split_new_letter_already_in_use_is_rejected()
    {
        var h = new EngineHarness(VirtualDisk());
        h.Discovery.State = h.Discovery.State with { ExternallyUsedLetters = new HashSet<char> { 'T' } };
        Assert.Equal("LETTER_IN_USE", Run(h, Req(Disk(h), WriteOperationKind.SplitPartition, size: 2 * GiB, fs: "NTFS", letter: 'T')).Code);
    }

    // ---- identity, concurrency, disconnection

    [Fact]
    public void Forged_disk_identity_is_rejected()
    {
        var h = new EngineHarness(VirtualDisk());
        var r = Req(Disk(h), WriteOperationKind.DeletePartition);
        r = r with { Disk = r.Disk with { UniqueId = "SOMEONE-ELSES-DISK" } };
        Assert.Equal("IDENTITY_NOTFOUND", Run(h, r).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Changed_partition_guid_is_rejected()
    {
        var h = new EngineHarness(VirtualDisk());
        var r = Req(Disk(h), WriteOperationKind.DeletePartition);
        r = r with { Partition = r.Partition! with { UniqueGuid = Guid.NewGuid() } };
        Assert.Equal("PARTITION_CHANGED", Run(h, r).Code);
    }

    [Fact]
    public void Unexpected_partition_boundaries_are_rejected()
    {
        var h = new EngineHarness(VirtualDisk());
        var r = Req(Disk(h), WriteOperationKind.ShrinkPartition, size: 2 * GiB);
        r = r with { Partition = r.Partition! with { OffsetBytes = r.Partition.OffsetBytes + MiB } };
        Assert.Equal("PARTITION_CHANGED", Run(h, r).Code);
    }

    [Fact]
    public void Concurrent_layout_change_before_execution_is_rejected()
    {
        var h = new EngineHarness(VirtualDisk());
        var r = Req(Disk(h), WriteOperationKind.SplitPartition, size: 2 * GiB, fs: "NTFS");
        h.Backend.CreatePartition(3, FreeStart, GiB); // another tool adds a partition in the meantime
        h.Backend.Calls.Clear();
        Assert.Equal("IDENTITY_LAYOUTCHANGED", Run(h, r).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Drive_letter_reassignment_does_not_redirect_the_operation()
    {
        // Partition 2 had Q: when planned; meanwhile its letter moves to R:. The target is pinned by GUID and
        // geometry, so the format still goes to partition 2 and nowhere else.
        var h = new EngineHarness(VirtualDisk());
        var r = Req(Disk(h), WriteOperationKind.FormatVolume, fs: "NTFS", label: "X");
        h.Discovery.Update(3, 2, p => p with { DriveLetter = 'R' });
        Assert.Equal(HelperResponseStatus.Ok, Run(h, r).Status);
        Assert.Contains("format 3/2 NTFS", h.Backend.Calls);
    }

    [Fact]
    public void Change_to_another_partition_during_the_operation_is_detected()
    {
        var d = VirtualDisk();
        d = d.WithPartitions(d.Partitions.Append(Part(3, 7, 6 * GiB, 1 * GiB, label: "Bystander") with { UniqueGuid = Guid.NewGuid() }));
        var h = new EngineHarness(d);
        h.Backend.AfterCall = call => { if (call.StartsWith("resize")) h.Discovery.Update(3, 7, p => p with { SizeBytes = p.SizeBytes - MiB }); };
        var r = Run(h, Req(Disk(h), WriteOperationKind.ShrinkPartition, size: 2 * GiB));
        Assert.Equal("OTHER_PARTITIONS_CHANGED", r.Code);
        Assert.Contains(h.Audit.Entries, e => e.Outcome == "UNEXPECTED_CHANGE");
    }

    [Fact]
    public void Disk_disconnected_mid_split_is_reported()
    {
        var h = new EngineHarness(VirtualDisk());
        h.Backend.AfterCall = call => { if (call.StartsWith("resize")) h.Discovery.State = new PreviewState([]); };
        var r = Run(h, Req(Disk(h), WriteOperationKind.SplitPartition, size: 2 * GiB, fs: "NTFS"));
        Assert.Equal("DISK_DISCONNECTED", r.Code);
        Assert.DoesNotContain(h.Backend.Calls, c => c.StartsWith("create"));
    }

    [Fact]
    public void Structural_operations_are_non_cancelable_after_commit() =>
        Assert.Equal(CancellationSemantics.NonCancelableAfterCommit, WriteOperationRequest.Cancellation(WriteOperationKind.SplitPartition));

    [Fact]
    public void Physical_disk_is_refused_for_every_structural_kind()
    {
        var disk = PhysicalDisk();
        var h = new EngineHarness(disk);
        foreach (var kind in new[] { WriteOperationKind.FormatVolume, WriteOperationKind.DeletePartition, WriteOperationKind.ShrinkPartition, WriteOperationKind.SplitPartition })
        {
            var r = new WriteOperationRequest(kind, DiskIdentity.From(disk), PartitionTarget.From(disk.Partitions[0]), null, null, null,
                SizeBytes: kind is WriteOperationKind.ShrinkPartition or WriteOperationKind.SplitPartition ? 5 * GiB : null,
                FileSystem: kind is WriteOperationKind.FormatVolume or WriteOperationKind.SplitPartition ? "NTFS" : null);
            Assert.Equal("PHYSICAL_DISK", Run(h, r).Code);
        }
        Assert.Empty(h.Backend.Calls);
    }
}

public class StructuralExecutorTests
{
    [Fact]
    public async Task Split_is_sent_through_the_pipe_and_reported()
    {
        await using var h = new SessionHarness();
        var s = State(VirtualDisk());
        var q = new OperationQueue(s, new SafetyPolicy());
        Assert.True(q.TryAdd(new SplitPartitionOperation(3, 2, 2 * GiB, "NTFS", "B", 'T')).Success);
        var report = await new HelperExecutor(_ => h.AuthenticatedClient(), NullOperationLog.Instance).ExecuteAsync(q.Items, s, null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Succeeded, report.Outcome);
        Assert.Contains(h.Engine.Backend.Calls, c => c.StartsWith("create"));
    }

    [Fact]
    public async Task Helper_crash_during_a_structural_operation_is_reported_as_uncertain()
    {
        var s = State(VirtualDisk());
        var q = new OperationQueue(s, new SafetyPolicy());
        Assert.True(q.TryAdd(new SplitPartitionOperation(3, 2, 2 * GiB, "NTFS", "B", null)).Success);
        var report = await new HelperExecutor(_ => Task.FromResult(new HelperClient(new MemoryStream())), NullOperationLog.Instance)
            .ExecuteAsync(q.Items, s, null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Failed, report.Outcome);
        Assert.Contains("may or may not have been applied", report.Error);
    }

    [Fact]
    public void Factory_maps_ui_operations_to_structural_requests()
    {
        var d = VirtualDisk();
        var s = State(d);
        var p = d.Partitions[0];
        Assert.Equal(WriteOperationKind.ShrinkPartition, WriteRequestFactory.TryCreate(new ResizeMovePartitionOperation(3, 2, p.OffsetBytes, 2 * GiB), s)!.Kind);
        Assert.Equal(WriteOperationKind.ExtendPartition, WriteRequestFactory.TryCreate(new ResizeMovePartitionOperation(3, 2, p.OffsetBytes, 5 * GiB), s)!.Kind);
        Assert.Equal(WriteOperationKind.SplitPartition, WriteRequestFactory.TryCreate(new SplitPartitionOperation(3, 2, 2 * GiB, "ntfs", "", 't'), s)!.Kind);
        var create = WriteRequestFactory.TryCreate(new CreatePartitionOperation(3, 16 * MiB + 4 * GiB, GiB, "NTFS", "N", 'u'), s)!;
        Assert.Null(create.Partition);
        Assert.Equal('U', create.NewDriveLetter);
        Assert.True(WriteRequestValidator.Validate(create).IsValid);
    }
}
