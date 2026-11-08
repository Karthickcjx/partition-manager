using System.Diagnostics;
using System.Text.Json;
using PartitionManager.Core.Discovery;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;
using PartitionManager.Core.PartitionTables;
using PartitionManager.Core.Recovery;
using PartitionManager.Infrastructure.WindowsApi;

namespace PartitionManager.PrivilegedHelper.Engine;

/// <summary>
/// Read-only data recovery inside the elevated helper, so the app never needs to run as administrator.
/// Never writes to any disk or path: the source is opened for reading only, and recovered bytes go back to the app,
/// which saves them as the signed-in user. The target is re-validated against fresh discovery on every scan.
/// </summary>
public sealed class RecoveryService(IDiskDiscoveryService discovery, IAuditSink audit,
    Func<PartitionInfo, VolumeReader>? openVolume = null, Func<DiskInfo, ISectorSource>? openDisk = null)
{
    private readonly Func<PartitionInfo, VolumeReader> _openVolume = openVolume ?? (p => RawVolumeReader.Open(p.VolumePath, p.SizeBytes));
    private readonly Func<DiskInfo, ISectorSource> _openDisk = openDisk ?? (d => RawDiskReader.Open(d.Number, d.LogicalSectorSize, d.SizeBytes));
    private NtfsScanResult? _scan;
    private PartitionInfo? _scanned;
    private Dictionary<long, RecoverableFile> _byRecord = [];

    public HelperResponse Handle(Guid requestId, RecoveryRequest? r)
    {
        if (r is null) return HelperResponse.Reject(requestId, "MISSING_RECOVERY", "Recovery requires a recovery request.");
        try
        {
            return r.Action switch
            {
                RecoveryAction.ScanVolume => Scan(requestId, r),
                RecoveryAction.ListFiles => List(requestId, r),
                RecoveryAction.ReadFile => Read(requestId, r),
                RecoveryAction.ScanLostPartitions => ScanLost(requestId, r),
                _ => HelperResponse.Reject(requestId, "UNKNOWN_ACTION", "Unknown recovery action."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NtfsFormatException or System.ComponentModel.Win32Exception)
        {
            return new HelperResponse(requestId, HelperResponseStatus.Failed, "RECOVERY_FAILED", ex.Message, []);
        }
    }

    private PreviewState Discover() => discovery.DiscoverAsync().GetAwaiter().GetResult().State;

    private HelperResponse Scan(Guid id, RecoveryRequest r)
    {
        var disk = Discover().Disks.FirstOrDefault(d => d.Number == r.DiskNumber);
        var p = disk?.FindPartition(r.PartitionNumber);
        if (p is null || p.OffsetBytes != r.PartitionOffset || p.SizeBytes != r.PartitionSize)
            return HelperResponse.Reject(id, "TARGET_CHANGED", "The partition no longer matches; refresh and choose it again.");
        if (!string.Equals(p.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(p.VolumePath))
            return HelperResponse.Reject(id, "UNSUPPORTED_FILE_SYSTEM", "Deleted-file recovery currently supports mounted NTFS volumes only.");
        _scan = null; _byRecord = [];
        var reader = _openVolume(p);
        try { _scan = NtfsRecoveryScanner.Scan(reader); }
        finally { (reader as IDisposable)?.Dispose(); }
        _scanned = p;
        _byRecord = _scan.Files.GroupBy(f => f.RecordNumber).ToDictionary(g => g.Key, g => g.First());
        audit.Record(id.ToString(), "RecoveryScan", $"Disk {disk!.Number} partition {p.PartitionNumber}", "READ_ONLY", $"{_scan.RecordsScanned} records, {_scan.Files.Count} deleted files");
        return Ok(id, new RecoveryScanSummary(_scan.RecordsScanned, _scan.CorruptRecords, _scan.Files.Count, _scan.Elapsed.TotalMilliseconds, _scan.Boot.ClusterSize));
    }

    private HelperResponse List(Guid id, RecoveryRequest r)
    {
        if (_scan is null) return HelperResponse.Reject(id, "NO_SCAN", "Scan a volume first.");
        var ordered = _scan.Files;
        var start = Math.Clamp(r.Start, 0, ordered.Count);
        var count = Math.Clamp(r.Count, 0, RecoveryRequest.MaxListPage);
        var page = ordered.Skip(start).Take(count).Select(RecoverableFileInfo.From).ToList();
        // Keep the reply under the message limit even with long Unicode names.
        while (page.Count > 1 && JsonSerializer.SerializeToUtf8Bytes(new RecoveryFilePage(start, ordered.Count, page), HelperProtocol.Json).Length > HelperProtocol.MaxMessageBytes - 4096)
            page.RemoveAt(page.Count - 1);
        return Ok(id, new RecoveryFilePage(start, ordered.Count, page));
    }

    private HelperResponse Read(Guid id, RecoveryRequest r)
    {
        if (_scan is null || _scanned is null) return HelperResponse.Reject(id, "NO_SCAN", "Scan a volume first.");
        if (!_byRecord.TryGetValue(r.RecordNumber, out var file)) return HelperResponse.Reject(id, "UNKNOWN_FILE", "The file is not in the last scan.");
        if (r.Position < 0 || r.Length <= 0) return HelperResponse.Reject(id, "BAD_RANGE", "Invalid read range.");
        var length = Math.Min(r.Length, RecoveryRequest.MaxReadBytes);
        var buffer = new byte[length];
        int n;
        var reader = _openVolume(_scanned);
        try { n = FileRecoverer.ReadAt(reader, _scan.Boot, file, r.Position, buffer, length); }
        finally { (reader as IDisposable)?.Dispose(); }
        var end = n < length || r.Position + n >= (file.Data?.RealSize ?? 0);
        return Ok(id, new RecoveryChunk(r.Position, n, end, Convert.ToBase64String(buffer, 0, n)));
    }

    private HelperResponse ScanLost(Guid id, RecoveryRequest r)
    {
        var disk = Discover().Disks.FirstOrDefault(d => d.Number == r.DiskNumber);
        if (disk is null) return HelperResponse.Reject(id, "TARGET_CHANGED", "The disk is no longer present.");
        var sw = Stopwatch.StartNew();
        IReadOnlyList<FoundPartition> found;
        var source = _openDisk(disk);
        try { found = LostPartitionScanner.ScanUnallocated(source, disk); }
        finally { (source as IDisposable)?.Dispose(); }
        audit.Record(id.ToString(), "LostPartitionScan", $"Disk {disk.Number}", "READ_ONLY", $"{found.Count} candidate(s)");
        return Ok(id, new LostPartitionList(found, sw.Elapsed.TotalMilliseconds));
    }

    private static HelperResponse Ok<T>(Guid id, T payload) =>
        new(id, HelperResponseStatus.Ok, "OK", "", []) { Payload = JsonSerializer.Serialize(payload, HelperProtocol.Json) };
}
