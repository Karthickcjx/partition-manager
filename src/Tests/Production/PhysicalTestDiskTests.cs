using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;
using Xunit;
using static PartitionManager.Tests.Fixtures;
using static PartitionManager.Tests.Production.ExecutionFixtures;

namespace PartitionManager.Tests.Production;

/// <summary>
/// Test-machine enrolment of a physical disk (HKLM PhysicalTestDiskIds). On the enrolled disk the running system
/// volume allows only in-place shrink/extend; other partitions and free space allow the real operation set;
/// Initialize and Merge stay refused; unenrolled physical disks are always refused.
/// </summary>
public class PhysicalTestDiskTests
{
    private const string Id = "PHYS-TEST-1";

    /// <summary>C: (boot, partition 2) plus a data partition R: (partition 3) and free space after it.</summary>
    private static DiskInfo EnrolledDisk()
    {
        var d = VirtualDisk(uniqueId: Id) with { IsVirtual = false, BusType = "NVMe", Location = "PCIROOT(0)#PCI(1F00)", SerialNumber = "SN-1" };
        var c = d.Partitions[0] with { IsBoot = true, Label = "Windows", DriveLetter = 'C' };
        var data = Part(3, 3, 5 * GiB, GiB, letter: 'R', free: GiB / 2, label: "Data") with { UniqueGuid = Guid.NewGuid(), GptType = PartitionTypes.BasicData };
        return d.WithPartitions([c, data]);
    }

    private static EngineHarness Harness(bool enrolled)
    {
        var h = new EngineHarness(EnrolledDisk());
        h.Policy = VirtualOnly with { PhysicalTestDiskIds = enrolled ? [Id] : [] };
        return h;
    }

    private static WriteOperationRequest Req(DiskInfo d, WriteOperationKind kind, int partition, long? size = null, long? offset = null, string? label = null, string? fs = null) =>
        new(kind, DiskIdentity.From(d), kind == WriteOperationKind.CreatePartition ? null : PartitionTarget.From(d.FindPartition(partition)!),
            label, null, null, OffsetBytes: offset, SizeBytes: size, FileSystem: fs);

    private static DiskInfo Disk(EngineHarness h) => h.Discovery.State.Disk(3);

    [Fact]
    public void System_volume_allows_in_place_shrink_and_extend()
    {
        var h = Harness(enrolled: true);
        var shrink = h.Engine.Execute(Guid.NewGuid(), Req(Disk(h), WriteOperationKind.ShrinkPartition, 2, size: 3 * GiB));
        Assert.True(shrink.Status == HelperResponseStatus.Ok, $"{shrink.Code}: {shrink.Message}");
        Assert.Contains("Enrolled physical test disk gate passed", shrink.Steps);
        var extend = h.Engine.Execute(Guid.NewGuid(), Req(Disk(h), WriteOperationKind.ExtendPartition, 2, size: 4 * GiB));
        Assert.True(extend.Status == HelperResponseStatus.Ok, $"{extend.Code}: {extend.Message}");
    }

    [Theory]
    [InlineData(WriteOperationKind.MovePartition)]
    [InlineData(WriteOperationKind.DeletePartition)]
    [InlineData(WriteOperationKind.FormatVolume)]
    [InlineData(WriteOperationKind.SetVolumeLabel)]
    public void System_volume_refuses_everything_but_resize(WriteOperationKind kind)
    {
        var h = Harness(enrolled: true);
        var d = Disk(h);
        var r = h.Engine.Execute(Guid.NewGuid(), kind switch
        {
            WriteOperationKind.MovePartition => Req(d, kind, 2, offset: 32 * MiB),
            WriteOperationKind.FormatVolume => Req(d, kind, 2, fs: "NTFS", label: "X"),
            WriteOperationKind.SetVolumeLabel => Req(d, kind, 2, label: "X"),
            _ => Req(d, kind, 2),
        });
        Assert.Equal("PHYSICAL_DISK", r.Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Free_space_and_data_partitions_get_real_operations()
    {
        var h = Harness(enrolled: true);
        var create = h.Engine.Execute(Guid.NewGuid(), Req(Disk(h), WriteOperationKind.CreatePartition, 0, size: GiB, offset: 6 * GiB + 16 * MiB, fs: "NTFS", label: "NEW"));
        Assert.True(create.Status == HelperResponseStatus.Ok, $"{create.Code}: {create.Message}");
        var label = h.Engine.Execute(Guid.NewGuid(), Req(Disk(h), WriteOperationKind.SetVolumeLabel, 3, label: "Renamed"));
        Assert.True(label.Status == HelperResponseStatus.Ok, $"{label.Code}: {label.Message}");
        var delete = h.Engine.Execute(Guid.NewGuid(), Req(Disk(h), WriteOperationKind.DeletePartition, 3));
        Assert.True(delete.Status == HelperResponseStatus.Ok, $"{delete.Code}: {delete.Message}");
    }

    [Fact]
    public void Unenrolled_physical_disk_is_refused()
    {
        var h = Harness(enrolled: false);
        var r = h.Engine.Execute(Guid.NewGuid(), Req(Disk(h), WriteOperationKind.ShrinkPartition, 2, size: 3 * GiB));
        Assert.Equal("PHYSICAL_DISK", r.Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Enrolment_is_by_exact_disk_id_never_allows_merge_and_initializes_only_blank_disks()
    {
        var d = EnrolledDisk();
        var data = d.FindPartition(3);
        Assert.False((VirtualOnly with { PhysicalTestDiskIds = ["OTHER"] }).AllowsPhysicalTest(d, WriteOperationKind.DeletePartition, data));
        Assert.False((HelperPolicy.Disabled with { PhysicalTestDiskIds = [Id] }).AllowsPhysicalTest(d, WriteOperationKind.DeletePartition, data));
        Assert.False((VirtualOnly with { PhysicalTestDiskIds = [Id] }).AllowsPhysicalTest(d with { UniqueId = "" }, WriteOperationKind.DeletePartition, data));
        var enrolled = VirtualOnly with { PhysicalTestDiskIds = [" phys-test-1 "] };
        Assert.True(enrolled.AllowsPhysicalTest(d, WriteOperationKind.DeletePartition, data));
        Assert.False(enrolled.AllowsPhysicalTest(d, WriteOperationKind.MergePartitions, data));
        // Initialize needs a blank, non-system disk, and only the public AllDisks mode approves physical ones.
        var blank = d.WithPartitions([]) with { Style = PartitionStyle.Raw, IsBoot = false, IsSystem = false };
        Assert.False(enrolled.ApprovesInitialization(blank, DateTimeOffset.UtcNow));
        Assert.True((enrolled with { Mode = WriteMode.AllDisks }).ApprovesInitialization(blank, DateTimeOffset.UtcNow));
        Assert.False((enrolled with { Mode = WriteMode.AllDisks }).ApprovesInitialization(d, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Eligibility_routes_free_space_create_as_real_and_system_delete_as_simulated()
    {
        var d = EnrolledDisk();
        var s = new PreviewState([d]);
        var policy = VirtualOnly with { PhysicalTestDiskIds = [Id] };
        var q = new OperationQueue(s, new PartitionManager.Core.Safety.SafetyPolicy());
        Assert.True(q.TryAdd(new ResizeMovePartitionOperation(3, 2, d.FindPartition(2)!.OffsetBytes, 3 * GiB)).Success);
        Assert.True(RealExecutionEligibility.Evaluate(q.Items, s, policy, true).IsEligible);
        var q2 = new OperationQueue(s, new PartitionManager.Core.Safety.SafetyPolicy());
        Assert.True(q2.TryAdd(new DeletePartitionOperation(3, 3)).Success);
        Assert.True(RealExecutionEligibility.Evaluate(q2.Items, s, policy, true).IsEligible, "data partition delete is real on an enrolled disk");
    }
}

/// <summary>0.6 public mode: every disk is writable, but the running system partitions keep the same limits.</summary>
public class AllDisksPolicyTests
{
    [Fact]
    public void All_disks_mode_writes_any_disk_but_still_protects_the_running_system()
    {
        var policy = new HelperPolicy(WriteMode.AllDisks, []);
        var d = PhysicalDisk();
        var sys = d.WithPartitions(d.Partitions.Select(p => p with { IsBoot = true }));
        Assert.True(policy.AllowsPhysicalTest(d, WriteOperationKind.DeletePartition, d.Partitions[0]));
        Assert.False(policy.AllowsPhysicalTest(sys, WriteOperationKind.DeletePartition, sys.Partitions[0]));
        Assert.True(policy.AllowsPhysicalTest(sys, WriteOperationKind.ShrinkPartition, sys.Partitions[0]));
        Assert.False(policy.AllowsPhysicalTest(d, WriteOperationKind.MergePartitions, d.Partitions[0]));
        Assert.True(policy.AllowsImage(@"C:\Users\me\disk.vhdx"));
        Assert.False(policy.AllowsImage(@"\server\share\disk.vhdx"));
        Assert.False(HelperPolicy.Disabled.AllowsImage(@"C:\Users\me\disk.vhdx"));
    }
}
