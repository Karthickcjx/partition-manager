namespace PartitionManager.Core.Models;

public enum PartitionStyle { Unknown, Raw, Mbr, Gpt }

public enum MediaKind { Unknown, Hdd, Ssd, Scm }

public enum HealthState { Unknown, Healthy, Warning, Unhealthy }

/// <summary>Immutable snapshot of a physical (or virtual) disk and its partitions.</summary>
public sealed record DiskInfo
{
    public required int Number { get; init; }
    public required string Model { get; init; }
    public required long SizeBytes { get; init; }
    public string SerialNumber { get; init; } = "";
    public string BusType { get; init; } = "Unknown";
    public MediaKind Media { get; init; }
    public PartitionStyle Style { get; init; }
    public int LogicalSectorSize { get; init; } = 512;
    public int PhysicalSectorSize { get; init; } = 512;
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }
    public bool IsVirtual { get; init; }
    public HealthState Health { get; init; }
    public string UniqueId { get; init; } = "";

    /// <summary>MSFT_Disk.Location: the bus location, or the backing image file path for attached VHD/VHDX disks.</summary>
    public string Location { get; init; } = "";

    /// <summary>Backing image file for a virtual disk; empty for physical disks.</summary>
    public string VirtualImagePath =>
        IsVirtual && (Location.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) || Location.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            ? Location : "";
    public IReadOnlyList<PartitionInfo> Partitions { get; init; } = Array.Empty<PartitionInfo>();

    /// <summary>True when the running Windows installation boots from or lives on this disk.</summary>
    public bool HostsRunningSystem => IsBoot || IsSystem || Partitions.Any(p => p.IsBoot || p.IsSystem);

    public string Title => $"Disk {Number}";

    public PartitionInfo? FindPartition(int partitionNumber) =>
        Partitions.FirstOrDefault(p => p.PartitionNumber == partitionNumber);

    public DiskInfo WithPartitions(IEnumerable<PartitionInfo> partitions) =>
        this with { Partitions = partitions.OrderBy(p => p.OffsetBytes).ThenBy(p => p.IsExtendedContainer ? 0 : 1).ToArray() };

    public int NextPartitionNumber() => Partitions.Count == 0 ? 1 : Partitions.Max(p => p.PartitionNumber) + 1;
}
