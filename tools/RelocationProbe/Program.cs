using System.Text.Json;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Logging;
using PartitionManager.Infrastructure.WindowsApi;
using PartitionManager.PrivilegedHelper.Engine;

namespace RelocationProbe;

/// <summary>
/// VALIDATION TOOL, NOT SHIPPED. Runs one real block-level Move through the production WriteEngine pipeline
/// (live discovery, HKLM policy, WMI backend, journal, layout verification), without the IPC session, on a disposable VHDX.
/// Refuses unless the disk is an attached file-backed virtual disk whose image is under --image-root and that root is also
/// approved by the HKLM helper policy; physical disks are refused by the engine itself regardless.
/// Usage: RelocationProbe --disk N --partition P --offset BYTES --image-root DIR --journal DIR
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var a = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i + 1 < args.Length; i += 2) a[args[i]] = args[i + 1];
        if (!a.TryGetValue("--disk", out var ds) || !a.TryGetValue("--partition", out var ps) || !a.TryGetValue("--offset", out var os) ||
            !a.TryGetValue("--image-root", out var root) || !a.TryGetValue("--journal", out var journalDir))
        {
            Console.Error.WriteLine("usage: RelocationProbe --disk N --partition P --offset BYTES --image-root DIR --journal DIR");
            return 64;
        }
        int diskNumber = int.Parse(ds), partitionNumber = int.Parse(ps);
        var offset = long.Parse(os);
        root = Path.GetFullPath(root).TrimEnd('\\') + "\\";

        var discovery = new WmiDiskDiscoveryService(NullOperationLog.Instance);
        var state = discovery.DiscoverAsync().GetAwaiter().GetResult().State;
        var disk = state.Disks.FirstOrDefault(d => d.Number == diskNumber);
        if (disk is null) return Fail(65, $"disk {diskNumber} not found");
        if (!disk.IsVirtual || disk.HostsRunningSystem || !disk.VirtualImagePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return Fail(65, $"REFUSED: disk {diskNumber} is not a disposable VHDX under {root} (virtual={disk.IsVirtual}, image='{disk.VirtualImagePath}', system={disk.HostsRunningSystem})");
        var partition = disk.FindPartition(partitionNumber);
        if (partition is null) return Fail(65, $"partition {partitionNumber} not found on disk {diskNumber}");

        Directory.CreateDirectory(journalDir);
        using var journal = new FileOperationJournal(journalDir);
        var audit = new FileLinesAudit(Path.Combine(journalDir, "audit.log"));
        var engine = new WriteEngine(discovery, new WmiStorageWriteBackend(), new RegistryHelperPolicySource(), audit) { Journal = journal }; // production default verify timeout
        var request = new WriteOperationRequest(WriteOperationKind.MovePartition, DiskIdentity.From(disk), PartitionTarget.From(partition), null, null, null, OffsetBytes: offset);
        var started = DateTime.UtcNow;
        var response = engine.Execute(Guid.NewGuid(), request);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            response.Status, response.Code, response.Message, response.Steps,
            Seconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
            From = partition.OffsetBytes, To = offset, partition.SizeBytes, partition.FileSystem,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return response.Status == HelperResponseStatus.Ok ? 0 : 1;
    }

    private static int Fail(int code, string message) { Console.Error.WriteLine(message); return code; }

    private sealed class FileLinesAudit(string path) : IAuditSink
    {
        public void Record(string requestId, string kind, string target, string outcome, string detail) =>
            File.AppendAllText(path, $"{DateTime.UtcNow:o}\t{requestId}\t{kind}\t{target}\t{outcome}\t{detail}{Environment.NewLine}");
    }
}
