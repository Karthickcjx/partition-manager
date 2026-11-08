using PartitionManager.Core.Discovery;
using PartitionManager.Core.Execution;
using PartitionManager.Core.FileSystems;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;
using PartitionManager.Infrastructure.WindowsApi;
using PartitionManager.PrivilegedHelper.Engine;
using static PartitionManager.Tests.Fixtures;

namespace PartitionManager.Tests.Production;

internal static class ExecutionFixtures
{
    public const string TestRoot = @"D:\pm-testdisks";
    public const string ImagePath = @"D:\pm-testdisks\disk1.vhdx";

    public static HelperPolicy VirtualOnly => new(WriteMode.VirtualDisksOnly, [TestRoot]);

    /// <summary>An attached VHDX with one NTFS data partition (E:, label "Data1").</summary>
    public static DiskInfo VirtualDisk(int number = 3, string image = ImagePath, string uniqueId = "VHD-UNIQUE-1") =>
        new DiskInfo
        {
            Number = number, Model = "Msft Virtual Disk", SizeBytes = 8 * GiB, Style = PartitionStyle.Gpt, BusType = "File Backed Virtual",
            IsVirtual = true, Location = image, UniqueId = uniqueId, SerialNumber = "", Health = HealthState.Healthy,
        }.WithPartitions([Part(number, 2, 16 * MiB, 4 * GiB, letter: 'Q', free: 3 * GiB, label: "Data1") with { UniqueGuid = Guid.Parse("11111111-2222-3333-4444-555555555555"), GptType = PartitionTypes.BasicData }]);

    public static DiskInfo PhysicalDisk() => DataDisk(1) with { UniqueId = "PHYS-1", SerialNumber = "SN-PHYS-1" };

    public static WriteOperationRequest LabelRequest(DiskInfo disk, string label = "NewLabel") =>
        new(WriteOperationKind.SetVolumeLabel, DiskIdentity.From(disk), PartitionTarget.From(disk.Partitions[0]), label, null, null);

    public static WriteOperationRequest ChangeLetterRequest(DiskInfo disk, char to = 'R') =>
        new(WriteOperationKind.ChangeDriveLetter, DiskIdentity.From(disk), PartitionTarget.From(disk.Partitions[0]), null, to, disk.Partitions[0].DriveLetter);
}

/// <summary>Discovery whose state the fake backend mutates, so post-write verification sees the change.</summary>
internal sealed class FakeDiscovery(params DiskInfo[] disks) : IDiskDiscoveryService
{
    public PreviewState State { get; set; } = new(disks);
    public int Calls { get; private set; }
    public Exception? FailWith { get; set; }

    public Task<DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        if (FailWith is not null) throw FailWith;
        return Task.FromResult(new DiscoveryResult(State, [], DateTimeOffset.Now));
    }

    public void Update(int disk, int partition, Func<PartitionInfo, PartitionInfo> change)
    {
        var d = State.Disk(disk);
        State = State.With(d.WithPartitions(d.Partitions.Select(p => p.PartitionNumber == partition ? change(p) : p)));
    }
}

internal sealed class FakeBackend(FakeDiscovery discovery) : IStorageWriteBackend
{
    public List<string> Calls { get; } = [];
    public bool Silent { get; set; }

    /// <summary>Runs after each call is recorded: simulate concurrent changes, disconnects and so on.</summary>
    public Action<string>? AfterCall { get; set; }
    public string? FailOn { get; set; }

    public void SetVolumeLabel(int d, int p, string label) => Do($"label {d}/{p} {label}", () => discovery.Update(d, p, x => x with { Label = label }));
    public void AddDriveLetter(int d, int p, char letter) => Do($"add {d}/{p} {letter}", () => discovery.Update(d, p, x => x with { DriveLetter = letter }));
    public void RemoveDriveLetter(int d, int p, char letter) => Do($"remove {d}/{p} {letter}", () => discovery.Update(d, p, x => x with { DriveLetter = null }));

    /// <summary>Override to simulate Windows reporting a different shrink/extend range.</summary>
    public Func<PartitionInfo, (long Min, long Max)>? SupportedSize { get; set; }

    public (long Min, long Max) GetSupportedSize(int d, int p)
    {
        Calls.Add($"supported {d}/{p}");
        var disk = discovery.State.Disk(d);
        var part = disk.FindPartition(p)!;
        if (SupportedSize is not null) return SupportedSize(part);
        var min = DiskLayout.AlignUp((part.UsedBytes ?? 0) + 16 * ByteSize.MiB);
        var next = disk.Partitions.Where(x => x.OffsetBytes > part.OffsetBytes).Select(x => x.OffsetBytes).DefaultIfEmpty(DiskLayout.UsableEnd(disk)).Min();
        return (Math.Min(min, part.SizeBytes), next - part.OffsetBytes);
    }

    public void Resize(int d, int p, long size) => Do($"resize {d}/{p} {size}", () => discovery.Update(d, p, x => x with
    {
        SizeBytes = size, VolumeSizeBytes = size, FreeBytes = size - (x.UsedBytes ?? 0),
    }));

    public int CreatePartition(int d, long offset, long size)
    {
        var disk = discovery.State.Disk(d);
        var number = disk.NextPartitionNumber();
        Do($"create {d} {offset}+{size}", () => discovery.State = discovery.State.With(disk.WithPartitions(disk.Partitions.Append(new PartitionInfo
        {
            DiskNumber = d, PartitionNumber = number, OffsetBytes = offset, SizeBytes = size, Role = PartitionRole.BasicData,
            GptType = PartitionTypes.BasicData, UniqueGuid = Guid.NewGuid(), BitLocker = BitLockerState.NotEncrypted,
        }))));
        return number;
    }

    public void Format(int d, int p, string fs, string label, int cluster, bool full) => Do($"format {d}/{p} {fs}", () =>
    {
        Formatted?.Invoke(discovery.State.Disk(d).FindPartition(p)!.UniqueGuid);
        discovery.Update(d, p, x => x with
        {
            FileSystem = FileSystemRules.Normalize(fs), Label = label, VolumeSizeBytes = x.SizeBytes, FreeBytes = x.SizeBytes,
            VolumePath = @"\\?\Volume{" + Guid.NewGuid() + @"}\",
        });
    });

    /// <summary>Exception thrown by RelocatePartition (simulates a locked volume or an I/O failure).</summary>
    public Exception? RelocateFailure { get; set; }
    /// <summary>Chunk size the fake reports checkpoints at.</summary>
    public long RelocateChunkBytes { get; set; } = 512 * ByteSize.MiB;
    /// <summary>MBR-style behaviour: the drive letter does not follow a moved partition.</summary>
    public bool RelocateDropsLetter { get; set; }

    public RelocationResult RelocatePartition(int d, PartitionTarget target, long offset, Action<long, long>? checkpoint, bool physicalTestDisk = false)
    {
        var p = target.PartitionNumber;
        RelocationResult result = new("AB".PadRight(64, '0'), "AB".PadRight(64, '0'), 0, 0, "test");
        Do($"relocate {d}/{p} {offset}", () =>
        {
            if (RelocateFailure is not null) throw RelocateFailure;
            var part = discovery.State.Disk(d).FindPartition(p)!;
            for (long at = 0; at < part.SizeBytes; at += RelocateChunkBytes) checkpoint?.Invoke(at, Math.Min(part.SizeBytes, at + RelocateChunkBytes));
            discovery.Update(d, p, x => x with { OffsetBytes = offset, DriveLetter = RelocateDropsLetter ? null : x.DriveLetter });
            result = result with { BytesCopied = part.SizeBytes, Chunks = (part.SizeBytes + RelocateChunkBytes - 1) / RelocateChunkBytes };
        });
        return result;
    }

    public HashSet<Guid?> ReadOnly { get; } = [];
    public Action<Guid?>? Formatted { get; set; }

    public void InitializeDisk(int d, PartitionStyle style) => Do($"initialize {d} {style}", () =>
        discovery.State = discovery.State.With(discovery.State.Disk(d) with { Style = style }));

    public void SetPartitionReadOnly(int d, int p, bool readOnly) => Do($"readonly {d}/{p} {readOnly}", () =>
    {
        var guid = discovery.State.Disk(d).FindPartition(p)!.UniqueGuid;
        if (readOnly) ReadOnly.Add(guid); else ReadOnly.Remove(guid);
    });

    public void DeletePartition(int d, int p) => Do($"delete {d}/{p}", () =>
    {
        var disk = discovery.State.Disk(d);
        discovery.State = discovery.State.With(disk.WithPartitions(disk.Partitions.Where(x => x.PartitionNumber != p)));
    });

    private void Do(string call, Action apply)
    {
        Calls.Add(call);
        AfterCall?.Invoke(call);
        if (FailOn is not null && call.StartsWith(FailOn, StringComparison.Ordinal)) { FailOn = null; throw new StorageWriteException("injected failure: " + call); }
        if (!Silent) apply();
    }
}

internal sealed class MemoryAudit : IAuditSink
{
    public List<(string Kind, string Outcome, string Detail)> Entries { get; } = [];
    public void Record(string requestId, string kind, string target, string outcome, string detail) => Entries.Add((kind, outcome, detail));
}

internal sealed class EngineHarness
{
    public FakeDiscovery Discovery { get; }
    public FakeBackend Backend { get; }
    public MemoryAudit Audit { get; } = new();
    public HelperPolicy Policy { get; set; } = ExecutionFixtures.VirtualOnly;
    public WriteEngine Engine { get; }
    public TestVolumeAccess Volumes { get; }

    internal WriteEngine EngineWith(CopyFaults faults) =>
        new(Discovery, Backend, new DelegatePolicy(() => Policy), Audit, Volumes) { VerifyTimeout = TimeSpan.FromMilliseconds(300), CopyFaults = faults, ExerciseUnreleasedStagingForTests = ExercisePrototype };

    public bool ExercisePrototype { get; init; }

    public EngineHarness(params DiskInfo[] disks) : this(false, disks) { }

    public EngineHarness(bool exercisePrototype, params DiskInfo[] disks)
    {
        Discovery = new FakeDiscovery(disks);
        Backend = new FakeBackend(Discovery);
        Volumes = new TestVolumeAccess(Discovery, Backend);
        ExercisePrototype = exercisePrototype;
        Engine = new WriteEngine(Discovery, Backend, new DelegatePolicy(() => Policy), Audit, Volumes) { VerifyTimeout = TimeSpan.FromMilliseconds(300), ExerciseUnreleasedStagingForTests = exercisePrototype };
    }

    private sealed class DelegatePolicy(Func<HelperPolicy> get) : IHelperPolicySource
    {
        public HelperPolicy Load() => get();
    }
}

/// <summary>Maps fake partitions' drive letters to real temp folders, so staged Move/Merge copy real files in tests.</summary>
internal sealed class TestVolumeAccess : VolumeAccess, IDisposable
{
    private readonly FakeDiscovery _discovery;
    private readonly FakeBackend _backend;
    public string BaseDir { get; } = Path.Combine(Path.GetTempPath(), "pm-vols-" + Guid.NewGuid().ToString("N"));
    public bool IgnoreReadOnly { get; set; }

    public TestVolumeAccess(FakeDiscovery discovery, FakeBackend backend)
    {
        _discovery = discovery;
        _backend = backend;
        backend.Formatted = guid => { var dir = DirFor(guid); if (Directory.Exists(dir)) DeleteAll(dir); Directory.CreateDirectory(dir); };
    }

    public string DirFor(Guid? guid) => Path.Combine(BaseDir, guid?.ToString("N") ?? "none");

    private PartitionInfo PartitionForLetter(char letter) =>
        _discovery.State.Disks.SelectMany(d => d.Partitions).Single(p => p.DriveLetter == char.ToUpperInvariant(letter));

    private PartitionInfo PartitionForRoot(string root) =>
        _discovery.State.Disks.SelectMany(d => d.Partitions).Single(p => string.Equals(DirFor(p.UniqueGuid), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));

    public override string RootOf(char letter)
    {
        var dir = DirFor(PartitionForLetter(letter).UniqueGuid);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public override bool CanWrite(string root) => IgnoreReadOnly || !_backend.ReadOnly.Contains(PartitionForRoot(root).UniqueGuid);

    public override long FreeBytes(string root)
    {
        var p = PartitionForRoot(root);
        var used = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        return p.SizeBytes - used;
    }

    public void Seed(PartitionInfo p, string rel, byte[] data)
    {
        var path = Path.Combine(DirFor(p.UniqueGuid), rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
    }

    private static void DeleteAll(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(dir, true);
    }

    public void Dispose() { if (Directory.Exists(BaseDir)) DeleteAll(BaseDir); }
}
