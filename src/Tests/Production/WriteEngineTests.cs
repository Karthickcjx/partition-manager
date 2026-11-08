using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;
using Xunit;
using static PartitionManager.Tests.Production.ExecutionFixtures;

namespace PartitionManager.Tests.Production;

public class WriteEngineTests
{
    private static HelperResponse Run(EngineHarness h, WriteOperationRequest r) => h.Engine.Execute(Guid.NewGuid(), r);

    [Fact]
    public void Label_change_is_written_verified_and_audited()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk);
        var resp = Run(h, LabelRequest(disk, "Renamed"));
        Assert.Equal(HelperResponseStatus.Ok, resp.Status);
        Assert.Equal(["label 3/2 Renamed"], h.Backend.Calls);
        Assert.Equal("Renamed", h.Discovery.State.Disk(3).Partitions[0].Label);
        Assert.Contains("Result verified", resp.Steps);
        Assert.Equal(["STARTED", "SUCCEEDED"], h.Audit.Entries.Select(e => e.Outcome));
    }

    [Fact]
    public void Letter_change_removes_then_adds()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk);
        Assert.Equal(HelperResponseStatus.Ok, Run(h, ChangeLetterRequest(disk, 'R')).Status);
        Assert.Equal(["remove 3/2 Q", "add 3/2 R"], h.Backend.Calls);
        Assert.Equal('R', h.Discovery.State.Disk(3).Partitions[0].DriveLetter);
    }

    [Fact]
    public void Assign_and_remove_letters()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk);
        var remove = new WriteOperationRequest(WriteOperationKind.RemoveDriveLetter, DiskIdentity.From(disk), PartitionTarget.From(disk.Partitions[0]), null, null, 'Q');
        Assert.Equal(HelperResponseStatus.Ok, Run(h, remove).Status);
        Assert.Null(h.Discovery.State.Disk(3).Partitions[0].DriveLetter);
        var now = h.Discovery.State.Disk(3);
        var assign = new WriteOperationRequest(WriteOperationKind.AssignDriveLetter, DiskIdentity.From(now), PartitionTarget.From(now.Partitions[0]), null, 'S', null);
        Assert.Equal(HelperResponseStatus.Ok, Run(h, assign).Status);
        Assert.Equal('S', h.Discovery.State.Disk(3).Partitions[0].DriveLetter);
    }

    [Fact]
    public void Physical_disk_is_rejected_without_writing()
    {
        var disk = PhysicalDisk() with { Partitions = PhysicalDisk().Partitions };
        var h = new EngineHarness(disk);
        var r = new WriteOperationRequest(WriteOperationKind.SetVolumeLabel, DiskIdentity.From(disk), PartitionTarget.From(disk.Partitions[0]), "x", null, null);
        var resp = Run(h, r);
        Assert.Equal("PHYSICAL_DISK", resp.Code);
        Assert.Empty(h.Backend.Calls);
        Assert.Contains(h.Audit.Entries, e => e.Outcome.StartsWith("REJECTED"));
    }

    [Fact]
    public void Disk_marked_virtual_but_image_outside_root_is_rejected()
    {
        var disk = VirtualDisk(image: @"C:\Windows\Temp\x.vhdx");
        var h = new EngineHarness(disk);
        Assert.Equal("IMAGE_NOT_APPROVED", Run(h, LabelRequest(disk)).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Request_naming_a_different_image_is_rejected()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk);
        var r = LabelRequest(disk);
        r = r with { Disk = r.Disk with { VirtualImagePath = @"D:\pm-testdisks\other.vhdx" } };
        Assert.Equal("IMAGE_MISMATCH", Run(h, r).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Disabled_policy_rejects_before_touching_disks()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk) { Policy = HelperPolicy.Disabled };
        Assert.Equal("WRITES_DISABLED", Run(h, LabelRequest(disk)).Code);
        Assert.Equal(0, h.Discovery.Calls);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Concurrent_layout_change_is_detected()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk);
        var request = LabelRequest(disk);
        h.Discovery.Update(3, 2, p => p with { SizeBytes = p.SizeBytes - ByteSize.MiB }); // someone resized it meanwhile
        Assert.Equal("IDENTITY_LAYOUTCHANGED", Run(h, request).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Disconnected_disk_is_detected()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk);
        var request = LabelRequest(disk);
        h.Discovery.State = new([]);
        Assert.Equal("IDENTITY_NOTFOUND", Run(h, request).Code);
    }

    [Fact]
    public void Identity_substitution_is_rejected()
    {
        var target = VirtualDisk(3);
        var other = VirtualDisk(4, @"D:\pm-testdisks\other.vhdx", "VHD-OTHER");
        var h = new EngineHarness(target, other);
        var r = LabelRequest(target);
        r = r with { Disk = r.Disk with { Number = 4 } }; // right identity, wrong number
        Assert.Equal("IDENTITY_DISKNUMBERCHANGED", Run(h, r).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Changed_partition_target_is_rejected()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk);
        var r = LabelRequest(disk);
        r = r with { Partition = r.Partition! with { UniqueGuid = Guid.NewGuid() } };
        Assert.Equal("PARTITION_CHANGED", Run(h, r).Code);
    }

    [Fact]
    public void Letter_already_changed_elsewhere_is_rejected()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk);
        var request = ChangeLetterRequest(disk, 'R');
        h.Discovery.Update(3, 2, p => p with { DriveLetter = 'T' });
        Assert.Equal("STATE_CHANGED", Run(h, request).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Letter_in_use_is_rejected()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk) ;
        h.Discovery.State = h.Discovery.State with { ExternallyUsedLetters = new HashSet<char> { 'R' } };
        Assert.Equal("LETTER_IN_USE", Run(h, ChangeLetterRequest(disk, 'R')).Code);
    }

    [Fact]
    public void Fat_label_rules_are_enforced_server_side()
    {
        var disk = VirtualDisk();
        disk = disk.WithPartitions(disk.Partitions.Select(p => p with { FileSystem = "FAT32" }));
        var h = new EngineHarness(disk);
        Assert.Equal("BAD_LABEL", Run(h, LabelRequest(disk, "TWELVECHARSX")).Code);
    }

    [Fact]
    public void Safety_policy_is_reapplied_in_the_helper()
    {
        var disk = VirtualDisk();
        disk = disk.WithPartitions(disk.Partitions.Select(p => p with { IsBoot = true }));
        var h = new EngineHarness(disk);
        Assert.Equal("SAFETY_POLICY", Run(h, ChangeLetterRequest(disk, 'R')).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Backend_failure_is_reported_and_audited()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk);
        h.Backend.FailOn = "label";
        var resp = Run(h, LabelRequest(disk));
        Assert.Equal(HelperResponseStatus.Failed, resp.Status);
        Assert.Equal("WRITE_FAILED", resp.Code);
        Assert.Contains(h.Audit.Entries, e => e.Outcome == "FAILED");
    }

    [Fact]
    public void Failed_second_half_of_letter_change_restores_the_old_letter()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk);
        h.Backend.FailOn = "add 3/2 R";
        var resp = Run(h, ChangeLetterRequest(disk, 'R'));
        Assert.Equal(HelperResponseStatus.Failed, resp.Status);
        Assert.Contains("was restored", resp.Message);
        Assert.Equal('Q', h.Discovery.State.Disk(3).Partitions[0].DriveLetter);
    }

    [Fact]
    public void Write_that_does_not_take_effect_fails_verification()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk);
        h.Backend.Silent = true;
        var resp = Run(h, LabelRequest(disk, "Renamed"));
        Assert.Equal("VERIFY_FAILED", resp.Code);
        Assert.Contains(h.Audit.Entries, e => e.Outcome == "UNVERIFIED");
    }

    [Fact]
    public void Discovery_failure_is_a_rejection()
    {
        var disk = VirtualDisk();
        var h = new EngineHarness(disk);
        h.Discovery.FailWith = new UnauthorizedAccessException("access denied");
        Assert.Equal("DISCOVERY_FAILED", Run(h, LabelRequest(disk)).Code);
    }

    [Fact]
    public void Malformed_request_never_reaches_discovery()
    {
        var h = new EngineHarness(VirtualDisk());
        Assert.Equal("BAD_LETTER", Run(h, ChangeLetterRequest(VirtualDisk(), 'A')).Code);
        Assert.Equal(0, h.Discovery.Calls);
    }
}
