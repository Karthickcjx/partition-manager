using System.Text.Json;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;
using PartitionManager.Core.Safety;
using PartitionManager.Infrastructure.WindowsApi;
using PartitionManager.Core.Logging;
using Xunit;
using static PartitionManager.Tests.Production.ExecutionFixtures;

namespace PartitionManager.Tests.Production;

public class Milestone3SecurityTests
{
    public static IEnumerable<object[]> AllWriteKinds => Enum.GetValues<WriteOperationKind>().Select(kind => new object[] { kind });

    [Theory]
    [MemberData(nameof(AllWriteKinds))]
    public void Every_allowlisted_kind_is_refused_on_a_physical_disk_before_backend_access(WriteOperationKind kind)
    {
        var disk = VirtualDisk() with { IsVirtual = false, Location = "" };
        var first = disk.Partitions[0] with { SizeBytes = 1L << 30 };
        var second = first with { PartitionNumber = 3, OffsetBytes = 3L << 30, UniqueGuid = Guid.NewGuid(), DriveLetter = 'R' };
        disk = disk.WithPartitions([first,second]);
        if(kind==WriteOperationKind.InitializeDisk) disk=disk with { Style=PartitionStyle.Raw,Partitions=[] };
        var request = new WriteOperationRequest(kind,DiskIdentity.From(disk),kind is WriteOperationKind.InitializeDisk or WriteOperationKind.CreatePartition ? null : PartitionTarget.From(first),null,null,null);
        request=kind switch {
            WriteOperationKind.SetVolumeLabel => request with { NewLabel="Test" },
            WriteOperationKind.AssignDriveLetter => request with { NewDriveLetter='Z' },
            WriteOperationKind.ChangeDriveLetter => request with { NewDriveLetter='Z',ExpectedCurrentDriveLetter='Q' },
            WriteOperationKind.RemoveDriveLetter => request with { ExpectedCurrentDriveLetter='Q' },
            WriteOperationKind.CreatePartition => request with { OffsetBytes=6L<<30,SizeBytes=1L<<30,FileSystem="NTFS" },
            WriteOperationKind.FormatVolume => request with { FileSystem="NTFS" },
            WriteOperationKind.ShrinkPartition => request with { SizeBytes=512L<<20 },
            WriteOperationKind.ExtendPartition => request with { SizeBytes=2L<<30 },
            WriteOperationKind.SplitPartition => request with { SizeBytes=512L<<20,FileSystem="NTFS" },
            WriteOperationKind.InitializeDisk => request with { NewStyle=PartitionStyle.Gpt },
            WriteOperationKind.MovePartition => request with { OffsetBytes=5L<<30 },
            WriteOperationKind.MergePartitions => request with { SecondPartition=PartitionTarget.From(second) },
            _ => request
        };
        var h=new EngineHarness(disk); using var cleanup=h.Volumes;
        Assert.Equal("PHYSICAL_DISK",h.Engine.Execute(Guid.NewGuid(),request).Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Replacement_disk_cannot_pass_post_write_verification()
    {
        var d = VirtualDisk(); var h = new EngineHarness(d); using var cleanup = h.Volumes;
        h.Backend.AfterCall = call => { if (call.StartsWith("delete")) h.Discovery.State = new([d with { UniqueId = "replacement", Partitions = [] }]); };
        var result = h.Engine.Execute(Guid.NewGuid(), new(WriteOperationKind.DeletePartition,DiskIdentity.From(d),PartitionTarget.From(d.Partitions[0]),null,null,null));
        Assert.Equal("DISK_REPLACED", result.Code);
        Assert.Equal(HelperResponseStatus.Failed,result.Status);
    }

    [Fact]
    public void Creator_owner_write_access_is_not_a_trusted_install_acl()
    {
        var rule = new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.CreatorOwnerSid,null),System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow);
        Assert.False(PartitionManager.PrivilegedHelper.Engine.InstallLocationCheck.Evaluate([rule],"test").IsValid);
    }

    // Move is now a verified block-level relocation (StagedEngineTests, BlockRelocationTests); Merge stays refused.
    [Theory]
    [InlineData(WriteOperationKind.MergePartitions)]
    public void Shipped_engine_rejects_migration_even_with_a_valid_virtual_request(WriteOperationKind kind)
    {
        var d = VirtualDisk();
        var p = d.Partitions[0];
        var second = p with { PartitionNumber = 3, UniqueGuid = Guid.NewGuid(), OffsetBytes = p.EndBytes, SizeBytes = 1L << 30 };
        d = d.WithPartitions([p, second]);
        var h = new EngineHarness(d);
        using var cleanup = h.Volumes;
        var r = new WriteOperationRequest(kind, DiskIdentity.From(d), PartitionTarget.From(p), null, null, null,
            OffsetBytes: kind == WriteOperationKind.MovePartition ? 5L << 30 : null,
            SecondPartition: kind == WriteOperationKind.MergePartitions ? PartitionTarget.From(second) : null);
        // Destination must fit the source size for static Move validation.
        if (kind == WriteOperationKind.MovePartition) r = r with { Partition = r.Partition! with { SizeBytes = 1L << 30 } };
        if (kind == WriteOperationKind.MovePartition)
        {
            d = d.WithPartitions([p with { SizeBytes = 1L << 30 }, second]);
            h.Discovery.State = new([d]);
            r = r with { Disk = DiskIdentity.From(d) };
        }
        var response = h.Engine.Execute(Guid.NewGuid(), r);
        Assert.Equal("UNSUPPORTED_OPERATION", response.Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Theory]
    [InlineData(-11)]
    [InlineData(1)]
    public void Initialization_refuses_stale_or_future_approval(int ageMinutes)
    {
        var d = VirtualDisk() with { Style = PartitionStyle.Raw, Partitions = [] };
        var h = new EngineHarness(d);
        using var cleanup = h.Volumes;
        h.Policy = h.Policy with { InitializationApprovals = [new(DiskIdentity.From(d), DateTimeOffset.UtcNow.AddMinutes(ageMinutes))] };
        var result = h.Engine.Execute(Guid.NewGuid(), new(WriteOperationKind.InitializeDisk, DiskIdentity.From(d), null, null, null, null, NewStyle: PartitionStyle.Gpt));
        Assert.Equal("INITIALIZATION_NOT_APPROVED", result.Code);
        Assert.Empty(h.Backend.Calls);
    }

    [Fact]
    public void Initialization_refuses_missing_ambiguous_or_changed_identity_approval()
    {
        var disk = VirtualDisk() with { Style = PartitionStyle.Raw, Partitions = [] };
        var approval = new BlankImageApproval(DiskIdentity.From(disk), DateTimeOffset.UtcNow);
        Assert.False(VirtualOnly.ApprovesInitialization(disk, DateTimeOffset.UtcNow));
        Assert.False((VirtualOnly with { InitializationApprovals = [approval, approval] }).ApprovesInitialization(disk, DateTimeOffset.UtcNow));
        Assert.False((VirtualOnly with { InitializationApprovals = [approval] }).ApprovesInitialization(disk with { UniqueId = "replacement" }, DateTimeOffset.UtcNow));
        Assert.False((VirtualOnly with { InitializationApprovals = [approval] }).ApprovesInitialization(disk with { Style = PartitionStyle.Unknown }, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(@"D:\pm-testdisks\a:payload.vhdx")]
    [InlineData(@"D:\pm-testdisks\alias.\a.vhdx")]
    [InlineData(@"D:\pm-testdisks\alias \a.vhdx")]
    [InlineData(@"D:\pm-testdisks\sub\..\a.vhdx")]
    public void Windows_path_aliases_are_rejected(string path) => Assert.False(VirtualOnly.AllowsImage(path));

    [Fact]
    public void Failed_shrink_that_actually_mutated_reports_uncertain_state()
    {
        var d = VirtualDisk();
        var h = new EngineHarness(d);
        using var cleanup = h.Volumes;
        h.Backend.AfterCall = call => { if (call.StartsWith("resize")) { h.Discovery.Update(d.Number, d.Partitions[0].PartitionNumber, p => p with { SizeBytes = 2L << 30 }); throw new IOException("lost reply after mutation"); } };
        var r = h.Engine.Execute(Guid.NewGuid(), new(WriteOperationKind.SplitPartition, DiskIdentity.From(d), PartitionTarget.From(d.Partitions[0]), "", null, null,
            SizeBytes: 2L << 30, FileSystem: "NTFS"));
        Assert.Equal("SPLIT_SHRINK_FAILED", r.Code);
        Assert.Contains("may have changed", r.Message);
        Assert.DoesNotContain("Nothing was changed", r.Message);
        Assert.Equal(2L << 30, h.Discovery.State.Disk(d.Number).Partitions[0].SizeBytes);
    }
}

public class EScopeRehearsalTests
{
    internal static DiskInfo Layout()
    {
        var d = VirtualDisk() with { SizeBytes = 512110190592, UniqueId = "synthetic-layout-only" };
        var extents = new (long Offset, long Size, PartitionRole Role, char? Letter)[] {
            (1048576,272629760,PartitionRole.EfiSystem,null), (273678336,16777216,PartitionRole.MicrosoftReserved,null),
            (290455552,160660004352,PartitionRole.BasicData,'C'), (160951173120,1062207488,PartitionRole.Recovery,null),
            (162014429184,289962721280,PartitionRole.BasicData,'D'), (451977150464,59860058112,PartitionRole.BasicData,'E'),
            (511837208576,272629760,PartitionRole.Recovery,null) };
        return d.WithPartitions(extents.Select((e,i) => new PartitionInfo { DiskNumber = d.Number, PartitionNumber = i+1,
            OffsetBytes = e.Offset, SizeBytes = e.Size, Role = e.Role, DriveLetter = e.Letter, UniqueGuid = Guid.NewGuid(),
            FileSystem = e.Letter is null ? "" : "NTFS", GptType = e.Role switch { PartitionRole.EfiSystem => PartitionTypes.EfiSystem,
                PartitionRole.MicrosoftReserved => PartitionTypes.MicrosoftReserved, PartitionRole.Recovery => PartitionTypes.WindowsRecovery, _ => PartitionTypes.BasicData } }));
    }

    private static DiskInfo Shrink(DiskInfo d) => d.WithPartitions(d.Partitions.Select(p => p.DriveLetter == 'E' ? p with { SizeBytes = p.SizeBytes - (1L << 30) } : p));
    private static RehearsalConfirmation Ticket(EScopeProposal p, DiskInfo d) => new(Guid.NewGuid(), EScopeRehearsal.ProposalHash(p,d), DateTimeOffset.UtcNow);

    [Fact]
    public void Fresh_confirmation_is_required_for_each_proposal_and_never_enables_writes()
    {
        var d = Layout(); var p = EScopeRehearsal.Inspect([d]); var proposed = Shrink(d);
        var rehearsal = new EScopeRehearsal(); var ticket = Ticket(p,proposed);
        Assert.True(rehearsal.EvaluateShrink(p,[d],proposed,ticket,DateTimeOffset.UtcNow).IsValid);
        Assert.Equal("REPLAY", rehearsal.EvaluateShrink(p,[d],proposed,ticket,DateTimeOffset.UtcNow).Code);
        Assert.False(p.EnablesPhysicalWrites);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(6)]
    public void Changing_any_protected_partition_is_refused(int index)
    {
        var d = Layout(); var p = EScopeRehearsal.Inspect([d]); var proposed = Shrink(d);
        proposed = proposed.WithPartitions(proposed.Partitions.Select((part,i) => i == index ? part with { IsHidden = !part.IsHidden } : part));
        Assert.False(new EScopeRehearsal().EvaluateShrink(p,[d],proposed,Ticket(p,proposed),DateTimeOffset.UtcNow).IsValid);
    }

    [Fact]
    public void Ambiguity_layout_changes_stale_confirmation_and_extent_escape_are_refused()
    {
        var d = Layout(); var policy = EScopeRehearsal.Inspect([d]); var proposed = Shrink(d);
        Assert.Throws<InvalidOperationException>(() => EScopeRehearsal.Inspect([d,d]));
        var rehearsal = new EScopeRehearsal();
        Assert.Equal("LAYOUT_CHANGED", rehearsal.EvaluateShrink(policy,[proposed],proposed,Ticket(policy,proposed),DateTimeOffset.UtcNow).Code);
        Assert.Equal("EXPIRED_CONFIRMATION", rehearsal.EvaluateShrink(policy,[d],proposed,Ticket(policy,proposed) with { CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(-3) },DateTimeOffset.UtcNow).Code);
        var moved = proposed.WithPartitions(proposed.Partitions.Select(p => p.DriveLetter == 'E' ? p with { OffsetBytes = p.OffsetBytes + (1 << 20) } : p));
        Assert.Equal("OUTSIDE_E_SCOPE", rehearsal.EvaluateShrink(policy,[d],moved,Ticket(policy,moved),DateTimeOffset.UtcNow).Code);
        Assert.Equal("WRONG_CONFIRMATION", rehearsal.EvaluateShrink(policy,[d],moved,Ticket(policy,proposed),DateTimeOffset.UtcNow).Code);
    }

    [Fact]
    public async Task Read_only_live_identity_inspection_produces_design_evidence()
    {
        var state = (await new WmiDiskDiscoveryService(NullOperationLog.Instance).DiscoverAsync()).State;
        if (!state.Disks.Any(d => d.Partitions.Any(p => p.DriveLetter == 'E'))) return;
        var proposal = EScopeRehearsal.Inspect(state.Disks);
        Assert.False(proposal.EnablesPhysicalWrites);
        var reportDir = Environment.GetEnvironmentVariable("PM_VHD_REPORT_DIR");
        if (!string.IsNullOrWhiteSpace(reportDir))
            File.WriteAllText(Path.Combine(reportDir, "e-scope-proposal-design-only.json"), JsonSerializer.Serialize(proposal, new JsonSerializerOptions { WriteIndented = true }));
    }
}
