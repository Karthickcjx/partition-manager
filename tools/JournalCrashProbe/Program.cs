using System.Text.Json;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;
using PartitionManager.PrivilegedHelper.Engine;

namespace JournalCrashProbe;
public static class Marker
{
    // Test-only process: no storage backend, device handles or elevation.
    public static void Main(string[] args)
    {
        var disk=JsonSerializer.Deserialize<DiskInfo>(File.ReadAllText(args[1]))!;
        using var journal=new FileOperationJournal(args[0]);
        journal.Begin(Guid.NewGuid(),new(WriteOperationKind.DeletePartition,DiskIdentity.From(disk),PartitionTarget.From(disk.Partitions[0]),null,null,null),disk,disk with { Partitions=[] });
        journal.Record("StepStarted","Test-only simulated mutation boundary",disk);
        Console.WriteLine("DURABLE"); Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
    }
}
