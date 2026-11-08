using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;
using PartitionManager.PrivilegedHelper.Engine;
using Xunit;
using static PartitionManager.Tests.Fixtures;
using static PartitionManager.Tests.Production.ExecutionFixtures;

namespace PartitionManager.Tests.Production;

public sealed class VerifiedCopierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pm-copy-" + Guid.NewGuid().ToString("N"));
    private string Src => Path.Combine(_root, "src");
    private string Dst => Path.Combine(_root, "dst");

    public VerifiedCopierTests() { Directory.CreateDirectory(Src); Directory.CreateDirectory(Dst); }

    private void Write(string rel, int size, int seed = 1)
    {
        var path = Path.Combine(Src, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    [Fact]
    public void Copies_unicode_deep_long_and_read_only_content_and_verifies_it()
    {
        Write("plain.txt", 10);
        Write("ünïcödé 文件 📁\\naïve ☃.bin", 5000, 2);
        var deep = string.Join('\\', Enumerable.Range(0, 30).Select(i => $"level{i:D2}"));
        Write(deep + "\\deep.bin", 100, 3);
        var longName = new string('x', 200);
        Write($"long\\{longName}\\{longName}.bin", 64, 4); // path well over 260 characters
        Write("ro.bin", 2048, 5);
        File.SetAttributes(Path.Combine(Src, "ro.bin"), FileAttributes.ReadOnly);
        Write("big.bin", 3 * 1024 * 1024 + 17, 6); // spans several 1 MiB buffers
        Directory.CreateDirectory(Path.Combine(Src, "empty folder"));
        File.SetLastWriteTimeUtc(Path.Combine(Src, "plain.txt"), new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc));

        var manifest = VerifiedCopier.Scan(Src);
        Assert.True(manifest.IsSupported, string.Join("; ", manifest.Problems));
        var copy = VerifiedCopier.Copy(manifest, Src, Dst, CancellationToken.None);
        Assert.Empty(VerifiedCopier.Verify(manifest, copy, Dst));
        Assert.True((File.GetAttributes(Path.Combine(Dst, "ro.bin")) & FileAttributes.ReadOnly) != 0);
        Assert.Equal(new DateTime(2020, 5, 6, 7, 8, 9, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(Dst, "plain.txt")));
        Assert.True(Directory.Exists(Path.Combine(Dst, "empty folder")));
        Assert.Equal(6, manifest.FileCount);
    }

    [Fact]
    public void Alternate_data_streams_are_refused_not_dropped()
    {
        Write("f.txt", 10);
        File.WriteAllText(Path.Combine(Src, "f.txt") + ":hidden", "secret"); // NTFS alternate data stream
        var manifest = VerifiedCopier.Scan(Src);
        Assert.Contains(manifest.Problems, p => p.Contains("alternate data streams"));
        Assert.Throws<CopyException>(() => VerifiedCopier.Copy(manifest, Src, Dst, CancellationToken.None));
    }

    [Fact]
    public void Hard_links_are_refused()
    {
        Write("a.bin", 10);
        Assert.True(CreateHardLink(Path.Combine(Src, "b.bin"), Path.Combine(Src, "a.bin"), IntPtr.Zero));
        Assert.Contains(VerifiedCopier.Scan(Src).Problems, p => p.Contains("hard links"));
    }

    [Fact]
    public void Junctions_are_refused()
    {
        Directory.CreateDirectory(Path.Combine(_root, "elsewhere"));
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(Src, "j")}\" \"{Path.Combine(_root, "elsewhere")}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        p.WaitForExit();
        Assert.Contains(VerifiedCopier.Scan(Src).Problems, x => x.Contains("reparse point"));
    }

    [Fact]
    public void Copy_failure_is_reported_with_the_file_name()
    {
        Write("a.bin", 10);
        Write("b.bin", 10);
        var manifest = VerifiedCopier.Scan(Src);
        var ex = Assert.Throws<CopyException>(() => VerifiedCopier.Copy(manifest, Src, Dst, CancellationToken.None,
            new CopyFaults { BeforeFile = rel => { if (rel == "b.bin") throw new IOException("There is not enough space on the disk."); } }));
        Assert.Contains("b.bin", ex.Message);
        Assert.Contains("not enough space", ex.Message);
    }

    [Fact]
    public void Tampered_copy_fails_verification()
    {
        Write("a.bin", 1000);
        var manifest = VerifiedCopier.Scan(Src);
        var copy = VerifiedCopier.Copy(manifest, Src, Dst, CancellationToken.None,
            new CopyFaults { AfterCopy = dst => { var b = File.ReadAllBytes(dst); b[0] ^= 0xFF; File.WriteAllBytes(dst, b); } });
        Assert.Contains(VerifiedCopier.Verify(manifest, copy, Dst), e => e.Contains("SHA-256"));
    }

    [Fact]
    public void Missing_destination_file_fails_verification()
    {
        Write("a.bin", 100);
        var manifest = VerifiedCopier.Scan(Src);
        var copy = VerifiedCopier.Copy(manifest, Src, Dst, CancellationToken.None);
        File.Delete(Path.Combine(Dst, "a.bin"));
        Assert.Contains(VerifiedCopier.Verify(manifest, copy, Dst), e => e.Contains("missing"));
    }

    [Fact]
    public void Locked_source_file_fails_the_copy()
    {
        Write("locked.bin", 100);
        var manifest = VerifiedCopier.Scan(Src);
        using var lockIt = new FileStream(Path.Combine(Src, "locked.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<CopyException>(() => VerifiedCopier.Copy(manifest, Src, Dst, CancellationToken.None));
    }

    [Fact]
    public void Many_files_copy_with_bounded_memory()
    {
        for (var i = 0; i < 2000; i++) Write($"many\\d{i % 40}\\f{i}.bin", 256, i);
        var manifest = VerifiedCopier.Scan(Src);
        var before = GC.GetTotalMemory(true);
        var copy = VerifiedCopier.Copy(manifest, Src, Dst, CancellationToken.None);
        Assert.Empty(VerifiedCopier.Verify(manifest, copy, Dst));
        Assert.Equal(2000, manifest.FileCount);
        Assert.True(GC.GetTotalMemory(true) - before < 64 * 1024 * 1024);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existing, IntPtr security);

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        try { Directory.Delete(_root, true); } catch { }
    }
}

/// <summary>Move, Merge and Initialize through the WriteEngine, with a fake storage backend but real file copies.</summary>
public sealed class StagedEngineTests : IDisposable
{
    private readonly List<EngineHarness> _harnesses = [];
    // 8 GiB virtual disk with a 2 GiB NTFS partition (Q:) at 16 MiB, so a same-size destination fits after it.
    private static readonly long FreeStart = 16 * MiB + 2 * GiB;

    private static DiskInfo Base()
    {
        var d = VirtualDisk();
        return d.WithPartitions(d.Partitions.Select(p => p with { SizeBytes = 2 * GiB, VolumeSizeBytes = 2 * GiB, FreeBytes = GiB }));
    }

    private EngineHarness New(params DiskInfo[] disks) { var h = new EngineHarness(true, disks.Length == 0 ? [Base()] : disks); _harnesses.Add(h); return h; }
    private static DiskInfo Disk(EngineHarness h) => h.Discovery.State.Disk(3);

    private static Dictionary<string, string> Seed(EngineHarness h, PartitionInfo p)
    {
        var result = new Dictionary<string, string>();
        foreach (var (rel, size) in new[] { ("a.bin", 1000), (@"sub\b.bin", 70_000), (@"sub\deeper\c.bin", 1) })
        {
            var data = new byte[size];
            new Random(size).NextBytes(data);
            h.Volumes.Seed(p, rel, data);
            result[rel] = Convert.ToHexString(SHA256.HashData(data));
        }
        return result;
    }

    private static void AssertFiles(EngineHarness h, PartitionInfo p, Dictionary<string, string> expected, string prefix = "")
    {
        foreach (var (rel, hash) in expected)
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(h.Volumes.DirFor(p.UniqueGuid), prefix, rel)))));
    }

    private static WriteOperationRequest MoveReq(DiskInfo d, PartitionInfo p, long dest) =>
        new(WriteOperationKind.MovePartition, DiskIdentity.From(d), PartitionTarget.From(p), null, null, null, OffsetBytes: dest);

    private static WriteOperationRequest MergeReq(DiskInfo d, PartitionInfo target, PartitionInfo source) =>
        new(WriteOperationKind.MergePartitions, DiskIdentity.From(d), PartitionTarget.From(target), null, null, null, SecondPartition: PartitionTarget.From(source));

    // ---- move (block-level; the copy algorithm itself is covered by BlockRelocationTests)

    [Fact]
    public void Move_relocates_blocks_and_keeps_guid_label_and_drive_letter()
    {
        var h = New();
        var src = Disk(h).FindPartition(2)!;
        var r = h.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h), src, FreeStart));
        Assert.True(r.Status == HelperResponseStatus.Ok, $"{r.Code}: {r.Message}");
        var moved = Disk(h).Partitions.Single();
        Assert.Equal((FreeStart, src.SizeBytes, src.UniqueGuid, 'Q', "NTFS", "Data1"),
            (moved.OffsetBytes, moved.SizeBytes, moved.UniqueGuid, moved.DriveLetter!.Value, moved.FileSystem, moved.Label));
        Assert.Equal([$"relocate 3/2 {FreeStart}"], h.Backend.Calls);   // no temporary partitions, no file copy, no delete
        Assert.Contains(r.Steps, s => s.StartsWith("Source and destination SHA-256 match"));
        Assert.Contains("Moved volume mounted with its file system, label and drive letter", r.Steps);
    }

    [Fact]
    public void Overlapping_move_within_the_partitions_own_range_is_allowed()
    {
        var h = New();
        var src = Disk(h).FindPartition(2)!;
        var r = h.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h), src, src.OffsetBytes + 64 * MiB));
        Assert.True(r.Status == HelperResponseStatus.Ok, $"{r.Code}: {r.Message}");
        Assert.Contains(r.Steps, s => s.Contains("overlapping block copy"));
        Assert.Equal(src.OffsetBytes + 64 * MiB, Disk(h).Partitions.Single().OffsetBytes);
    }

    [Fact]
    public void Move_into_occupied_or_too_small_space_is_rejected()
    {
        var d = Base();
        d = d.WithPartitions(d.Partitions.Append(Part(3, 3, FreeStart + GiB, GiB) with { UniqueGuid = Guid.NewGuid() }));
        var h = New(d);
        var src = Disk(h).FindPartition(2)!;
        Assert.Equal("NOT_ENOUGH_SPACE", h.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h), src, FreeStart)).Code);         // only 1 GiB free there
        Assert.Equal("NOT_ENOUGH_SPACE", h.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h), src, FreeStart + GiB)).Code);   // onto the other partition
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Move_of_unsupported_file_system_or_protected_partition_is_rejected()
    {
        var d = Base();
        var h = New(d.WithPartitions(d.Partitions.Select(p => p with { FileSystem = "ReFS" })));
        Assert.Equal("UNSUPPORTED_FILE_SYSTEM", h.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h), Disk(h).FindPartition(2)!, FreeStart)).Code);
        var h2 = New(d.WithPartitions(d.Partitions.Select(p => p with { Role = PartitionRole.Recovery })));
        Assert.Equal("PROTECTED_PARTITION", h2.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h2), Disk(h2).FindPartition(2)!, FreeStart)).Code);
        var h3 = New(d.WithPartitions(d.Partitions.Select(p => p with { BitLocker = BitLockerState.Encrypted })));
        Assert.Equal("ENCRYPTED", h3.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h3), Disk(h3).FindPartition(2)!, FreeStart)).Code);
        Assert.Empty(h.Backend.Calls.Concat(h2.Backend.Calls).Concat(h3.Backend.Calls));
    }

    [Fact]
    public void Move_with_changed_layout_is_rejected()
    {
        var h = New();
        var src = Disk(h).FindPartition(2)!;
        var req = MoveReq(Disk(h), src, FreeStart);
        h.Backend.CreatePartition(3, FreeStart + 2 * GiB, GiB);
        h.Backend.Calls.Clear();
        Assert.Equal("IDENTITY_LAYOUTCHANGED", h.Engine.Execute(Guid.NewGuid(), req).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Volume_in_use_is_reported_as_nothing_changed()
    {
        var h = New();
        var src = Disk(h).FindPartition(2)!;
        h.Backend.RelocateFailure = new IOException("The volume is in use (lock failed, error 5). Close every program using it, then retry. Nothing was changed.");
        var r = h.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h), src, FreeStart));
        Assert.Equal("MOVE_NOT_STARTED", r.Code);
        Assert.Contains("Nothing was changed.", r.Message);
        Assert.DoesNotContain("partly overwritten", r.Message);
        Assert.Equal(src.OffsetBytes, Disk(h).Partitions.Single().OffsetBytes);
    }

    [Fact]
    public void Failure_of_a_non_overlapping_copy_says_the_original_is_intact()
    {
        var h = New();
        var src = Disk(h).FindPartition(2)!;
        h.Backend.RelocateFailure = new RelocationFailedException(new RelocationProgress { FirstWriteIssued = true, BytesCopied = 64 * MiB }, new IOException("injected device error"));
        var r = h.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h), src, FreeStart));
        Assert.Equal("MOVE_FAILED", r.Code);
        Assert.Contains("The original partition was not overwritten", r.Message);
    }

    [Fact]
    public void Failure_of_an_overlapping_copy_warns_that_the_original_may_be_damaged()
    {
        var h = New();
        var src = Disk(h).FindPartition(2)!;
        h.Backend.RelocateFailure = new RelocationFailedException(new RelocationProgress { Overlapping = true, MountGuardSet = true, FirstWriteIssued = true, BytesCopied = 64 * MiB }, new IOException("injected device error"));
        var r = h.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h), src, src.OffsetBytes + 64 * MiB));
        Assert.Equal("MOVE_INTERRUPTED", r.Code);
        Assert.Contains("partly overwritten", r.Message);
        Assert.Contains("Windows will not mount it", r.Message);
        Assert.DoesNotContain("Nothing was changed", r.Message);
    }

    [Theory]
    [InlineData("The requested resource is in use.")]               // ERROR_BUSY text after writes must NOT read as "nothing changed"
    [InlineData("The disk is in use or locked by another process.")]
    public void In_use_text_after_writes_is_never_reported_as_nothing_changed(string win32Text)
    {
        var h = New();
        var src = Disk(h).FindPartition(2)!;
        h.Backend.RelocateFailure = new RelocationFailedException(new RelocationProgress { Overlapping = true, FirstWriteIssued = true, CopyVerified = true, SourceSha256 = new string('A', 64) }, new IOException(win32Text));
        var r = h.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h), src, src.OffsetBytes + 64 * MiB));
        Assert.Equal("MOVE_NOT_SWITCHED", r.Code);
        Assert.Contains("complete copy was verified", r.Message);
        Assert.DoesNotContain("Nothing was changed", r.Message);
    }

    [Fact]
    public void Lost_disk_after_the_table_switch_is_reported_as_uncertain_not_unchanged()
    {
        var h = New();
        var src = Disk(h).FindPartition(2)!;
        h.Backend.RelocateFailure = new PostMutationException("Disk disconnected after mutation; inspect journal before recovery.");
        var r = h.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h), src, FreeStart));
        Assert.Equal("MOVE_UNCERTAIN", r.Code);
        Assert.DoesNotContain("Nothing was changed", r.Message);
    }

    [Fact]
    public void Drive_letter_is_restored_when_it_does_not_follow_the_partition()
    {
        var h = New();
        var src = Disk(h).FindPartition(2)!;
        h.Backend.RelocateDropsLetter = true;
        var r = h.Engine.Execute(Guid.NewGuid(), MoveReq(Disk(h), src, FreeStart));
        Assert.True(r.Status == HelperResponseStatus.Ok, $"{r.Code}: {r.Message}");
        Assert.Equal('Q', Disk(h).Partitions.Single().DriveLetter);
        Assert.Contains("add 3/2 Q", h.Backend.Calls);
    }

    [Fact]
    public void Journaled_relocation_records_a_durable_checkpoint_per_chunk()
    {
        var h = New();
        var disk = Disk(h);
        var journal = new RecordingJournal();
        var backend = new JournaledBackend(h.Backend, journal, () => Disk(h), disk);
        backend.RelocatePartition(3, PartitionTarget.From(disk.FindPartition(2)!), FreeStart, null);
        Assert.Equal(["RelocationPlanned", "StepStarted", "ChunkCopied", "ChunkCopied", "ChunkCopied", "ChunkCopied", "StepReturned"], journal.Phases);
    }

    private sealed class RecordingJournal : IOperationJournal
    {
        public List<string> Phases { get; } = [];
        public void Begin(Guid id, WriteOperationRequest request, DiskInfo initial, DiskInfo intended) => Phases.Add("Prepared");
        public void Record(string phase, string detail, DiskInfo? layout = null) => Phases.Add(phase);
    }

    // ---- merge

    private static DiskInfo TwoPartitions(long secondOffset)
    {
        var d = Base();
        return d.WithPartitions(d.Partitions.Append(Part(3, 3, secondOffset, GiB, letter: 'R', free: GiB - 10 * MiB, label: "Second") with { UniqueGuid = Guid.NewGuid() }));
    }

    [Fact]
    public void Merge_adjacent_copies_into_a_new_folder_deletes_source_and_extends()
    {
        var h = New(TwoPartitions(FreeStart));
        var target = Disk(h).FindPartition(2)!;
        var source = Disk(h).FindPartition(3)!;
        h.Volumes.Seed(target, @"Merged from Second\already-here.txt", [1, 2, 3]); // name clash with the default folder
        var files = Seed(h, source);
        var r = h.Engine.Execute(Guid.NewGuid(), MergeReq(Disk(h), target, source));
        Assert.Equal(HelperResponseStatus.Ok, r.Status);
        var merged = Disk(h).Partitions.Single();
        Assert.Equal(target.UniqueGuid, merged.UniqueGuid);
        Assert.Equal(source.EndBytes - target.OffsetBytes, merged.SizeBytes);
        AssertFiles(h, merged, files, "Merged from Second (2)");                  // existing folder untouched, new one used
        Assert.True(File.Exists(Path.Combine(h.Volumes.DirFor(merged.UniqueGuid), "Merged from Second", "already-here.txt")));
    }

    [Fact]
    public void Merge_nonadjacent_keeps_freed_space_unallocated()
    {
        var h = New(TwoPartitions(FreeStart + GiB));
        var r = h.Engine.Execute(Guid.NewGuid(), MergeReq(Disk(h), Disk(h).FindPartition(2)!, Disk(h).FindPartition(3)!));
        Assert.Equal(HelperResponseStatus.Ok, r.Status);
        Assert.Contains("unallocated", r.Message);
        Assert.Equal(2 * GiB, Disk(h).Partitions.Single().SizeBytes);
    }

    [Fact]
    public void Merge_without_enough_free_space_is_rejected()
    {
        var d = TwoPartitions(FreeStart);
        d = d.WithPartitions(d.Partitions.Select(p => p.PartitionNumber == 2 ? p with { FreeBytes = 10 * MiB } : p with { FreeBytes = 0 }));
        var h = New(d);
        Assert.Equal("NOT_ENOUGH_SPACE", h.Engine.Execute(Guid.NewGuid(), MergeReq(Disk(h), Disk(h).FindPartition(2)!, Disk(h).FindPartition(3)!)).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Merge_copy_failure_removes_partial_folder_and_keeps_both()
    {
        var h = New(TwoPartitions(FreeStart));
        var target = Disk(h).FindPartition(2)!;
        var source = Disk(h).FindPartition(3)!;
        var files = Seed(h, source);
        var engine = h.EngineWith(new CopyFaults { BeforeFile = rel => { if (rel.EndsWith("c.bin")) throw new UnauthorizedAccessException("Access is denied."); } });
        var r = engine.Execute(Guid.NewGuid(), MergeReq(Disk(h), target, source));
        Assert.Equal(HelperResponseStatus.Failed, r.Status);
        Assert.Contains("merge folder on target: undone", r.Message);
        Assert.Equal(2, Disk(h).Partitions.Count);
        Assert.False(Directory.Exists(Path.Combine(h.Volumes.DirFor(target.UniqueGuid), "Merged from Second")));
        AssertFiles(h, source, files);
    }

    [Fact]
    public void Merge_into_itself_or_with_unsupported_file_system_is_rejected()
    {
        var h = New(TwoPartitions(FreeStart));
        var p2 = Disk(h).FindPartition(2)!;
        Assert.Equal("SAME_PARTITION", h.Engine.Execute(Guid.NewGuid(), MergeReq(Disk(h), p2, p2)).Code);
        var d = TwoPartitions(FreeStart);
        var h2 = New(d.WithPartitions(d.Partitions.Select(p => p.PartitionNumber == 3 ? p with { FileSystem = "" } : p)));
        Assert.Equal("UNSUPPORTED_FILE_SYSTEM", h2.Engine.Execute(Guid.NewGuid(), MergeReq(Disk(h2), Disk(h2).FindPartition(2)!, Disk(h2).FindPartition(3)!)).Code);
    }

    // ---- initialize

    [Theory]
    [InlineData(PartitionStyle.Gpt)]
    [InlineData(PartitionStyle.Mbr)]
    public void Initialize_blank_virtual_disk(PartitionStyle style)
    {
        var blank = VirtualDisk() with { Style = PartitionStyle.Raw, Partitions = [] };
        var h = New(blank);
        h.Policy = h.Policy with { InitializationApprovals = [new(DiskIdentity.From(blank), DateTimeOffset.UtcNow)] };
        var r = h.Engine.Execute(Guid.NewGuid(), new(WriteOperationKind.InitializeDisk, DiskIdentity.From(blank), null, null, null, null, NewStyle: style));
        Assert.Equal(HelperResponseStatus.Ok, r.Status);
        Assert.Equal(style, Disk(h).Style);
    }

    [Fact]
    public void Initialize_refuses_disks_that_already_have_a_table_or_partitions()
    {
        var h = New();
        Assert.Equal("INITIALIZATION_NOT_APPROVED", h.Engine.Execute(Guid.NewGuid(), new(WriteOperationKind.InitializeDisk, DiskIdentity.From(Disk(h)), null, null, null, null, NewStyle: PartitionStyle.Gpt)).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Initialize_refuses_physical_disks()
    {
        var phys = PhysicalDisk() with { Style = PartitionStyle.Raw, Partitions = [] };
        var h = New(phys);
        Assert.Equal("PHYSICAL_DISK", h.Engine.Execute(Guid.NewGuid(), new(WriteOperationKind.InitializeDisk, DiskIdentity.From(phys), null, null, null, null, NewStyle: PartitionStyle.Gpt)).Code);
    }

    [Theory]
    [InlineData(PartitionStyle.Raw)]
    [InlineData(null)]
    public void Initialize_needs_gpt_or_mbr(PartitionStyle? style)
    {
        var blank = VirtualDisk() with { Style = PartitionStyle.Raw, Partitions = [] };
        Assert.Equal("BAD_STYLE", WriteRequestValidator.Validate(new(WriteOperationKind.InitializeDisk, DiskIdentity.From(blank), null, null, null, null, NewStyle: style)).Code);
    }

    public void Dispose() { foreach (var h in _harnesses) h.Volumes.Dispose(); }
}
