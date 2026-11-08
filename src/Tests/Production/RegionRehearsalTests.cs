using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;
using PartitionManager.Core.Safety;
using Xunit;

namespace PartitionManager.Tests.Production;

public class RegionPolicyTests
{
    private static (DiskInfo Disk,PartitionInfo E,RegionRehearsal Policy) Setup()
    {
        var d=EScopeRehearsalTests.Layout(); var e=d.Partitions.Single(p=>p.DriveLetter=='E'); return(d,e,new(d,e.UniqueGuid!.Value));
    }
    private static ValidationResult Apply(RegionRehearsal p,DiskInfo current,DiskInfo proposed)=>p.ValidateAndAdvance([current],proposed,new(Guid.NewGuid(),p.ConfirmationHash(proposed),DateTimeOffset.UtcNow),DateTimeOffset.UtcNow);

    [Fact]
    public void Shrink_then_create_tracks_new_guid_without_widening_original_region()
    {
        var(d,e,p)=Setup(); var shrunk=d.WithPartitions(d.Partitions.Select(x=>x==e?x with {SizeBytes=e.SizeBytes-(1L<<30)}:x));
        Assert.True(Apply(p,d,shrunk).IsValid);
        var created=e with { PartitionNumber=8,UniqueGuid=Guid.NewGuid(),OffsetBytes=e.EndBytes-(1L<<30),SizeBytes=1L<<30,DriveLetter='R' };
        var after=shrunk.WithPartitions(shrunk.Partitions.Append(created));
        Assert.True(Apply(p,shrunk,after).IsValid); Assert.Contains(created.UniqueGuid!.Value,p.OwnedPartitions);
        Assert.Equal(e.OffsetBytes,p.OriginalStart); Assert.Equal(e.EndBytes,p.OriginalEndExclusive); Assert.False(p.EnablesPhysicalWrites);
        var outside=after.WithPartitions(after.Partitions.Select(x=>x==created?x with {SizeBytes=2L<<30}:x));
        Assert.False(Apply(p,after,outside).IsValid);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(6)]
    public void Every_protected_partition_rejects_metadata_changes(int index)
    {
        var(d,_,p)=Setup(); var proposed=d.WithPartitions(d.Partitions.Select((x,i)=>i==index?x with {DriveLetter='Z'}:x));
        Assert.Equal("PROTECTED_PARTITION_CHANGED",Apply(p,d,proposed).Code);
    }

    [Fact]
    public void Physical_disk_cannot_enroll_even_when_E_exists()
    {
        var(d,e,_)=Setup(); Assert.Throws<ArgumentException>(()=>new RegionRehearsal(d with {IsVirtual=false},e.UniqueGuid!.Value));
    }

    [Fact]
    public void Letter_change_keeps_original_guid_scope_and_cannot_redirect_to_D()
    {
        var(d,e,p)=Setup(); var renamed=d.WithPartitions(d.Partitions.Select(x=>x==e?x with {DriveLetter='Q'}:x));
        Assert.True(Apply(p,d,renamed).IsValid);
        var redirected=renamed.WithPartitions(renamed.Partitions.Select(x=>x.DriveLetter=='D'?x with {SizeBytes=x.SizeBytes-(1L<<30)}:x));
        Assert.False(Apply(p,renamed,redirected).IsValid);
    }

    [Fact]
    public void Renumbering_replay_stale_and_modified_proposal_fail_closed()
    {
        var(d,e,p)=Setup(); var after=d.WithPartitions(d.Partitions.Select(x=>x==e?x with {SizeBytes=x.SizeBytes-(1L<<30)}:x));
        var ticket=new RehearsalConfirmation(Guid.NewGuid(),p.ConfirmationHash(after),DateTimeOffset.UtcNow);
        Assert.Equal("WRONG_CONFIRMATION",p.ValidateAndAdvance([d],after with {UniqueId="forged"},ticket,DateTimeOffset.UtcNow).Code);
        Assert.Equal("REPLAY",p.ValidateAndAdvance([d],after,ticket,DateTimeOffset.UtcNow).Code);
        Assert.Equal("LAYOUT_CHANGED",p.ValidateAndAdvance([d with {Number=9}],after,ticket with {Nonce=Guid.NewGuid()},DateTimeOffset.UtcNow).Code);
        Assert.Equal("EXPIRED_CONFIRMATION",p.ValidateAndAdvance([d],after,ticket with {Nonce=Guid.NewGuid(),CreatedUtc=DateTimeOffset.UtcNow.AddMinutes(-3)},DateTimeOffset.UtcNow).Code);
    }
}
