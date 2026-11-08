using System.Management;
using PartitionManager.Core.Discovery;
using PartitionManager.Core.FileSystems;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;

namespace PartitionManager.Infrastructure.WindowsApi;

/// <summary>
/// Enumerates disks through the Windows Storage Management API (WMI namespace root\Microsoft\Windows\Storage:
/// MSFT_Disk, MSFT_PhysicalDisk, MSFT_Partition, MSFT_Volume). Every query is read-only and works without elevation.
/// </summary>
public sealed class WmiDiskDiscoveryService(IOperationLog log, IBitLockerProbe? bitLocker = null) : IDiskDiscoveryService
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    public Task<DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Discover(cancellationToken), cancellationToken);

    private DiscoveryResult Discover(CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var warnings = new List<string>();
        var scope = new ManagementScope(StorageNamespace);
        scope.Connect();

        var physical = Query(scope, "SELECT DeviceId, MediaType, SpindleSpeed FROM MSFT_PhysicalDisk")
            .GroupBy(o => Str(o, "DeviceId"))
            .ToDictionary(g => g.Key, g => g.First());
        var volumes = Query(scope, "SELECT Path, DriveLetter, FileSystemLabel, FileSystem, Size, SizeRemaining FROM MSFT_Volume").ToList();
        var partitionsByDisk = Query(scope,
                "SELECT DiskNumber, PartitionNumber, DriveLetter, Offset, Size, MbrType, GptType, IsHidden, IsActive, IsBoot, IsSystem, AccessPaths, Guid FROM MSFT_Partition")
            .GroupBy(o => (int)U32(o, "DiskNumber"))
            .ToDictionary(g => g.Key, g => g.ToList());
        ct.ThrowIfCancellationRequested();

        var bitlocker = new Dictionary<char, BitLockerState>();
        if (bitLocker is not null)
        {
            try { bitlocker = bitLocker.Probe(); }
            catch (Exception ex) { warnings.Add("BitLocker status unavailable: " + ex.Message); }
        }

        var disks = new List<DiskInfo>();
        foreach (var d in Query(scope,
                     "SELECT Number, FriendlyName, Model, SerialNumber, BusType, PartitionStyle, Size, LogicalSectorSize, PhysicalSectorSize, " +
                     "IsBoot, IsSystem, IsOffline, IsReadOnly, HealthStatus, UniqueId, Location FROM MSFT_Disk"))
        {
            ct.ThrowIfCancellationRequested();
            var number = (int)U32(d, "Number");
            physical.TryGetValue(number.ToString(), out var pd);
            var busType = BusTypeName(U16(d, "BusType"));
            var disk = new DiskInfo
            {
                Number = number,
                Model = FirstNonEmpty(Str(d, "FriendlyName"), Str(d, "Model"), $"Disk {number}"),
                SerialNumber = Str(d, "SerialNumber").Trim(),
                BusType = busType,
                Media = pd is null ? MediaKind.Unknown : MediaFrom(U16(pd, "MediaType")),
                Style = U16(d, "PartitionStyle") switch { 1 => PartitionStyle.Mbr, 2 => PartitionStyle.Gpt, 0 => PartitionStyle.Raw, _ => PartitionStyle.Unknown },
                SizeBytes = (long)U64(d, "Size"),
                LogicalSectorSize = (int)Math.Max(512u, U32(d, "LogicalSectorSize")),
                PhysicalSectorSize = (int)Math.Max(512u, U32(d, "PhysicalSectorSize")),
                IsBoot = Bool(d, "IsBoot"),
                IsSystem = Bool(d, "IsSystem"),
                IsOffline = Bool(d, "IsOffline"),
                IsReadOnly = Bool(d, "IsReadOnly"),
                IsVirtual = busType is "File Backed Virtual" or "Virtual",
                Health = U16(d, "HealthStatus") switch { 0 => HealthState.Healthy, 1 => HealthState.Warning, 2 => HealthState.Unhealthy, _ => HealthState.Unknown },
                UniqueId = Str(d, "UniqueId"),
                Location = Str(d, "Location"),
            };

            var parts = partitionsByDisk.TryGetValue(number, out var list) ? list : [];
            disk = MarkLogicals(disk.WithPartitions(parts.Select(p => MapPartition(disk, p, volumes, bitlocker))));
            disks.Add(disk);
        }

        var partitionLetters = disks.SelectMany(x => x.Partitions).Where(p => p.DriveLetter is not null).Select(p => p.DriveLetter!.Value).ToHashSet();
        var external = DriveInfo.GetDrives()
            .Select(di => char.ToUpperInvariant(di.Name[0]))
            .Where(c => !partitionLetters.Contains(c))
            .ToHashSet();

        log.Info("discovery", $"found {disks.Count} disk(s), {disks.Sum(x => x.Partitions.Count)} partition(s) in {started.ElapsedMilliseconds} ms");
        return new DiscoveryResult(new PreviewState(disks.OrderBy(x => x.Number).ToArray()) { ExternallyUsedLetters = external }, warnings, DateTimeOffset.Now);
    }

    private static PartitionInfo MapPartition(DiskInfo disk, ManagementBaseObject p, List<ManagementBaseObject> volumes, Dictionary<char, BitLockerState> bitlocker)
    {
        var gptText = Str(p, "GptType");
        Guid? gpt = Guid.TryParse(gptText, out var g) ? g : null;
        var mbrRaw = U16(p, "MbrType");
        byte? mbr = disk.Style == PartitionStyle.Mbr && mbrRaw <= 0xFF ? (byte)mbrRaw : null;
        var letterCode = U16(p, "DriveLetter");
        char? letter = letterCode is >= 'A' and <= 'Z' or >= 'a' and <= 'z' ? char.ToUpperInvariant((char)letterCode) : null;

        var role = gpt is { } gg ? PartitionTypes.RoleFromGpt(gg) : mbr is { } mm ? PartitionTypes.RoleFromMbr(mm) : PartitionRole.Unknown;
        var accessPaths = p["AccessPaths"] as string[] ?? [];
        var vol = volumes.FirstOrDefault(v => accessPaths.Any(a => string.Equals(a, Str(v, "Path"), StringComparison.OrdinalIgnoreCase)));

        var info = new PartitionInfo
        {
            DiskNumber = disk.Number,
            PartitionNumber = (int)U32(p, "PartitionNumber"),
            OffsetBytes = (long)U64(p, "Offset"),
            SizeBytes = (long)U64(p, "Size"),
            Role = role,
            GptType = gpt,
            MbrType = mbr,
            IsActive = Bool(p, "IsActive"),
            IsHidden = Bool(p, "IsHidden"),
            IsBoot = Bool(p, "IsBoot"),
            IsSystem = Bool(p, "IsSystem"),
            DriveLetter = letter,
            Label = vol is null ? "" : Str(vol, "FileSystemLabel"),
            FileSystem = vol is null ? "" : FileSystemRules.Normalize(Str(vol, "FileSystem")),
            VolumeSizeBytes = vol is null ? null : (long)U64(vol, "Size"),
            FreeBytes = vol is null ? null : (long)U64(vol, "SizeRemaining"),
            VolumePath = vol is null ? "" : Str(vol, "Path"),
            UniqueGuid = Guid.TryParse(Str(p, "Guid"), out var ug) ? ug : null,
            BitLocker = letter is { } c && bitlocker.TryGetValue(c, out var bl) ? bl : (vol is null ? BitLockerState.NotEncrypted : BitLockerState.Unknown),
        };
        return info;
    }

    /// <summary>Marks MBR partitions that sit inside the extended container as logical.</summary>
    internal static DiskInfo MarkLogicals(DiskInfo disk)
    {
        if (disk.Style != PartitionStyle.Mbr) return disk;
        var ext = disk.Partitions.FirstOrDefault(p => p.IsExtendedContainer);
        if (ext is null) return disk;
        return disk.WithPartitions(disk.Partitions.Select(p =>
            !p.IsExtendedContainer && p.OffsetBytes >= ext.OffsetBytes && p.EndBytes <= ext.EndBytes ? p with { IsLogical = true } : p));
    }

    private static IEnumerable<ManagementBaseObject> Query(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        return results.Cast<ManagementBaseObject>().ToList();
    }

    private static object? Prop(ManagementBaseObject o, string name)
    {
        try { return o[name]; } catch (ManagementException) { return null; }
    }

    private static string Str(ManagementBaseObject o, string name) => Prop(o, name)?.ToString() ?? "";
    private static bool Bool(ManagementBaseObject o, string name) => Prop(o, name) is bool b && b;
    private static ushort U16(ManagementBaseObject o, string name) => Prop(o, name) is { } v ? Convert.ToUInt16(v) : (ushort)0xFFFF;
    private static uint U32(ManagementBaseObject o, string name) => Prop(o, name) is { } v ? Convert.ToUInt32(v) : 0;
    private static ulong U64(ManagementBaseObject o, string name) => Prop(o, name) is { } v ? Convert.ToUInt64(v) : 0;

    private static string FirstNonEmpty(params string[] values) => values.First(v => !string.IsNullOrWhiteSpace(v)).Trim();

    private static MediaKind MediaFrom(ushort code) => code switch { 3 => MediaKind.Hdd, 4 => MediaKind.Ssd, 5 => MediaKind.Scm, _ => MediaKind.Unknown };

    /// <summary>STORAGE_BUS_TYPE values (ntddstor.h) as reported by MSFT_Disk.BusType.</summary>
    internal static string BusTypeName(ushort code) => code switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "File Backed Virtual",
        16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS",
        _ => "Unknown",
    };
}
