using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;
using Xunit;
using static PartitionManager.Tests.Fixtures;
using static PartitionManager.Tests.Production.ExecutionFixtures;

namespace PartitionManager.Tests.Production;

/// <summary>
/// Test-machine enrolment of a physical disk (HKLM PhysicalTestDiskIds): only in-place shrink/extend through
/// MSFT_Partition.Resize is allowed, only on the enrolled disk ID; everything else on physical disks stays refused.
/// </summary>
public class PhysicalTestDiskTests
{
    private const string Id = "PHYS-TEST-1";

    private static DiskInfo SystemDisk() =>
        VirtualDisk(uniqueId: Id) with { IsVirtual = false, BusType = "NVMe", Location = "PCIROOT(0)#PCI(1F00)", SerialNumber = "SN-1" };

    private static EngineHarness Harness(bool enrolled)
    {
        var h = new EngineHarness(SystemDisk());
        h.Policy = VirtualOnly with { PhysicalTestDiskIds = enrolled ? [Id] : [] };
        return h;
    }

    private static WriteOperationRequest Req(DiskInfo d, WriteOperationKind kind, long? size = null, long? offset = null, string? label = null, string? fs = null) =>
        new(kind, DiskIdentity.From(d), PartitionTarget.From(d.FindPartition(2)!), label, null, null, OffsetBytes: offset, SizeBytes: size, FileSystem: fs);

    [Fact]
    public void Enrolled_disk_allows_in_place_shrink_and_extend()
    {
        var h = Harness(enrolled: true);
        var d = h.Discovery.State.Disk(3);
        var shrink = h.Engine.Execute(Guid.NewGuid(), Req(d, WriteOperationKind.ShrinkPartition, size: 3 * GiB));
        Assert.True(shrink.Status == HelperResponseStatus.Ok, $"{shrink.Code}: {shrink.Message}");
        Assert.Contains("Enrolled physical test disk: in-place resize gate passed", shrink.Steps);
        var extend = h.Engine.Execute(Guid.NewGuid(), Req(h.Discovery.State.Disk(3), WriteOperationKind.ExtendPartition, size: 4 * GiB));
        Assert.True(extend.Status == HelperResponseStatus.Ok, $"{extend.Code}: {extend.Message}");
    }

    [Theory]
    [InlineData(WriteOperationKind.MovePartition)]
    [InlineData(WriteOperationKind.DeletePartition)]
    [InlineData(WriteOperationKind.FormatVolume)]
    [InlineData(WriteOperationKind.SetVolumeLabel)]
    public void Enrolled_disk_still_refuses_everything_else(WriteOperationKind kind)
    {
        var h = Harness(enrolled: true);
        var d = h.Discovery.State.Disk(3);
        var r = h.Engine.Execute(Guid.NewGuid(), kind switch
        {
            WriteOperationKind.MovePartition => Req(d, kind, offset: 5 * GiB),
            WriteOperationKind.FormatVolume => Req(d, kind, fs: "NTFS", label: "X"),
            WriteOperationKind.SetVolumeLabel => Req(d, kind, label: "X"),
            _ => Req(d, kind),
        });
        Assert.NotEqual(HelperResponseStatus.Ok, r.Status);
        Assert.DoesNotContain(h.Backend.Calls, c => !c.StartsWith("supported"));
    }

    [Fact]
    public void Unenrolled_physical_disk_is_refused()
    {
        var h = Harness(enrolled: false);
        var r = h.Engine.Execute(Guid.NewGuid(), Req(h.Discovery.State.Disk(3), WriteOperationKind.ShrinkPartition, size: 3 * GiB));
        Assert.Equal("PHYSICAL_DISK", r.Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Enrolment_is_by_exact_disk_id_and_needs_the_virtual_disks_mode()
    {
        var d = SystemDisk();
        Assert.False((VirtualOnly with { PhysicalTestDiskIds = ["OTHER"] }).AllowsPhysicalResize(d, WriteOperationKind.ShrinkPartition));
        Assert.False((HelperPolicy.Disabled with { PhysicalTestDiskIds = [Id] }).AllowsPhysicalResize(d, WriteOperationKind.ShrinkPartition));
        Assert.False((VirtualOnly with { PhysicalTestDiskIds = [Id] }).AllowsPhysicalResize(d with { UniqueId = "" }, WriteOperationKind.ShrinkPartition));
        Assert.True((VirtualOnly with { PhysicalTestDiskIds = [" phys-test-1 "] }).AllowsPhysicalResize(d, WriteOperationKind.ExtendPartition));
    }

    [Fact]
    public void Eligibility_routes_enrolled_shrink_as_real_and_delete_as_simulated()
    {
        var d = SystemDisk();
        var s = new PreviewState([d]);
        var policy = VirtualOnly with { PhysicalTestDiskIds = [Id] };
        var p = d.FindPartition(2)!;
        var queue = new OperationQueue(s, new PartitionManager.Core.Safety.SafetyPolicy());
        Assert.True(queue.TryAdd(new ResizeMovePartitionOperation(3, 2, p.OffsetBytes, 3 * GiB)).Success);
        Assert.True(RealExecutionEligibility.Evaluate(queue.Items, s, policy, true).IsEligible);
        var q2 = new OperationQueue(s, new PartitionManager.Core.Safety.SafetyPolicy());
        q2.TryAdd(new DeletePartitionOperation(3, 2));
        Assert.False(RealExecutionEligibility.Evaluate(q2.Items, s, policy, true).IsEligible);
    }
}
