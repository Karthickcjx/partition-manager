using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Logging;
using PartitionManager.Infrastructure.WindowsApi;
using Xunit;

namespace PartitionManager.Tests.Production;

public class PerformanceMeasurements
{
    [Fact]
    public async Task Read_only_enumeration_and_loopback_pipe_measurements()
    {
        var samples=new List<double>(); var discovery=new WmiDiskDiscoveryService(NullOperationLog.Instance);
        for(var i=0;i<5;i++) { var timer=Stopwatch.StartNew(); var result=await discovery.DiscoverAsync(); timer.Stop(); Assert.NotEmpty(result.State.Disks); samples.Add(timer.Elapsed.TotalMilliseconds); }
        var name="PM.Perf."+Guid.NewGuid().ToString("N");
        using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous);
        using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var connect=server.WaitForConnectionAsync(timeout.Token); await client.ConnectAsync(timeout.Token); await connect;
        var echo=Task.Run(async()=>{for(var i=0;i<100;i++) { var r=await HelperProtocol.ReadAsync<HelloReply>(server,timeout.Token); await HelperProtocol.WriteAsync(server,r!,timeout.Token); }});
        var latencies=new List<double>();
        for(var i=0;i<100;i++) { var timer=Stopwatch.StartNew(); await HelperProtocol.WriteAsync(client,new HelloReply(true,"OK","loopback",false,"Disabled"),timeout.Token); Assert.NotNull(await HelperProtocol.ReadAsync<HelloReply>(client,timeout.Token)); latencies.Add(timer.Elapsed.TotalMilliseconds); }
        await echo;
        var folder=Environment.GetEnvironmentVariable("PM_VHD_REPORT_DIR");
        if(!string.IsNullOrWhiteSpace(folder)) File.WriteAllText(Path.Combine(folder,"m4-enumeration-ipc-performance.json"),JsonSerializer.Serialize(new {
            CapturedUtc=DateTimeOffset.UtcNow,EnumerationMs=samples,LoopbackPipeMs=latencies,
            Limitation="In-process named-pipe protocol round trips, not elevated helper startup or authenticated installed-app latency; no physical writes." },new JsonSerializerOptions{WriteIndented=true}));
    }
}
