using System.Diagnostics;
using System.Text.Json;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;
using PartitionManager.Infrastructure.WindowsApi;
using PartitionManager.PrivilegedHelper.Engine;
using Xunit;
using static PartitionManager.Tests.Production.ExecutionFixtures;

namespace PartitionManager.Tests.Production;

public sealed class JournalTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"pm-journal-"+Guid.NewGuid().ToString("N"));
    public JournalTests()=>Directory.CreateDirectory(_root);
    private WriteEngine Engine(EngineHarness h,IOperationJournal journal)=>new(h.Discovery,h.Backend,new FixedHelperPolicySource(h.Policy),h.Audit,h.Volumes) { Journal=journal,VerifyTimeout=TimeSpan.FromMilliseconds(100) };

    [Fact]
    public void Verified_operation_has_durable_intent_steps_observed_layout_and_final_status()
    {
        using var journal=new FileOperationJournal(_root); var d=VirtualDisk(); var h=new EngineHarness(d); using var volumes=h.Volumes;
        var result=Engine(h,journal).Execute(Guid.NewGuid(),LabelRequest(d));
        Assert.Equal(HelperResponseStatus.Ok,result.Status);
        var history=Assert.Single(FileOperationJournal.Inspect(_root));
        Assert.True(history.Valid); Assert.False(history.RequiresReview);
        Assert.Equal(new[] {"Prepared","StepStarted","StepReturned","Verified"},history.Events.Select(e=>e.Phase));
        Assert.NotNull(history.Events[0].Request); Assert.NotNull(history.Events[0].IntendedLayout);
        Assert.Equal("NewLabel",history.Events[^1].Layout!.Partitions[0].Label);
    }

    [Fact]
    public async Task Real_process_termination_retains_flushed_intent_and_blocks_restart()
    {
        var diskFile=Path.Combine(_root,"disk.json"); var d=VirtualDisk(); File.WriteAllText(diskFile,JsonSerializer.Serialize(d));
        var dotnet=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".dotnet","dotnet.exe");
        var runtime=Path.Combine(AppContext.BaseDirectory,"PartitionManager.Tests.runtimeconfig.json");
        using var child=Process.Start(new ProcessStartInfo(dotnet) {
            ArgumentList={"exec","--runtimeconfig",runtime,typeof(JournalCrashProbe.Marker).Assembly.Location,_root,diskFile},
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true })!;
        try
        {
            var ready=child.StandardOutput.ReadLineAsync();
            Assert.Equal("DURABLE", await ready.WaitAsync(TimeSpan.FromSeconds(20)));
        }
        finally { if(!child.HasExited) child.Kill(); child.WaitForExit(); }
        using var restarted=new FileOperationJournal(_root);
        var history=Assert.Single(FileOperationJournal.Inspect(_root)); Assert.True(history.RequiresReview); Assert.True(history.Valid);
        var h=new EngineHarness(d); using var volumes=h.Volumes;
        var response=Engine(h,restarted).Execute(Guid.NewGuid(),LabelRequest(d));
        Assert.Equal(HelperResponseStatus.Rejected,response.Status); Assert.Contains("RECOVERY_REVIEW_REQUIRED",response.Message);
        Assert.Empty(h.Backend.Calls);
    }

    [Theory]
    [InlineData("{torn")]
    [InlineData("\n")]
    public void Corrupt_or_torn_tail_is_never_treated_as_success(string tail)
    {
        var d=VirtualDisk();
        using(var journal=new FileOperationJournal(_root)) { journal.Begin(Guid.NewGuid(),LabelRequest(d),d,d); journal.Record("Verified","test"); }
        File.AppendAllText(Directory.GetFiles(_root,"operation-*.jsonl").Single(),tail);
        var history=Assert.Single(FileOperationJournal.Inspect(_root)); Assert.False(history.Valid); Assert.True(history.RequiresReview);
    }

    /// <summary>Found in RDP testing: an open Data Recovery helper session blocked every partition operation (exit 70).</summary>
    [Fact]
    public void Read_only_helper_does_not_hold_the_journal_lease()
    {
        var d=VirtualDisk();
        using var recoveryHelper=new LazyOperationJournal(()=>new FileOperationJournal(_root));   // never begins a write
        using var writer=new LazyOperationJournal(()=>new FileOperationJournal(_root));
        writer.Begin(Guid.NewGuid(),LabelRequest(d),d,d);                                         // succeeds alongside it
        writer.Record("Verified","test");
        using var second=new LazyOperationJournal(()=>new FileOperationJournal(_root));
        var ex=Assert.Throws<IOException>(()=>second.Begin(Guid.NewGuid(),LabelRequest(d),d,d));  // a second writer is refused
        Assert.Contains("Another privileged helper",ex.Message);
        Assert.Contains("Nothing was changed",ex.Message);
    }

    [Fact]
    public void Journal_writer_is_exclusive_across_sessions()
    {
        using var first=new FileOperationJournal(_root);
        Assert.Throws<IOException>(()=>new FileOperationJournal(_root));
    }

    [Theory]
    [InlineData("Prepared")]
    [InlineData("StepStarted")]
    [InlineData("StepReturned")]
    [InlineData("Verified")]
    public void Persistence_failure_never_reports_success_or_retries(string fail)
    {
        using var durable=new FileOperationJournal(_root);
        var journal=new FaultJournal(durable,fail); var d=VirtualDisk(); var h=new EngineHarness(d); using var volumes=h.Volumes;
        var result=Engine(h,journal).Execute(Guid.NewGuid(),LabelRequest(d));
        Assert.NotEqual(HelperResponseStatus.Ok,result.Status);
        Assert.Equal(fail is "Prepared" or "StepStarted" ? 0 : 1,h.Backend.Calls.Count);
        if(fail!="Prepared") Assert.True(Assert.Single(FileOperationJournal.Inspect(_root)).RequiresReview);
    }

    [Fact]
    public void Failed_split_records_partial_state_and_blocks_further_writes()
    {
        using var journal=new FileOperationJournal(_root); var d=VirtualDisk(); var h=new EngineHarness(d); using var volumes=h.Volumes;
        h.Backend.FailOn="create";
        var engine=Engine(h,journal);
        var result=engine.Execute(Guid.NewGuid(),new(WriteOperationKind.SplitPartition,DiskIdentity.From(d),PartitionTarget.From(d.Partitions[0]),"",null,null,SizeBytes:2L<<30,FileSystem:"NTFS"));
        Assert.Equal(HelperResponseStatus.Failed,result.Status);
        var history=Assert.Single(FileOperationJournal.Inspect(_root)); Assert.True(history.RequiresReview);
        Assert.Equal("Failed",history.Events[^1].Phase); Assert.Equal(2L<<30,history.Events[^1].Layout!.Partitions[0].SizeBytes);
        var calls=h.Backend.Calls.Count;
        var retry=engine.Execute(Guid.NewGuid(),LabelRequest(h.Discovery.State.Disk(d.Number)));
        Assert.Equal(HelperResponseStatus.Rejected,retry.Status); Assert.Equal(calls,h.Backend.Calls.Count);
    }

    private sealed class FaultJournal(IOperationJournal inner,string fail) : IOperationJournal
    {
        public void Begin(Guid id,WriteOperationRequest r,DiskInfo initial,DiskInfo intended) { if(fail=="Prepared") throw new IOException("injected disk full"); inner.Begin(id,r,initial,intended); }
        public void Record(string phase,string detail,DiskInfo? layout=null) { if(phase==fail) throw new IOException("injected journal I/O failure"); inner.Record(phase,detail,layout); }
    }
    public void Dispose()=>Directory.Delete(_root,true);
}
