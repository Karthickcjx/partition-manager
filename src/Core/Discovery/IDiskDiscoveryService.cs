using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;

namespace PartitionManager.Core.Discovery;

public sealed record DiscoveryResult(PreviewState State, IReadOnlyList<string> Warnings, DateTimeOffset Timestamp);

/// <summary>Read-only enumeration of disks, partitions and volumes.</summary>
public interface IDiskDiscoveryService
{
    Task<DiscoveryResult> DiscoverAsync(CancellationToken cancellationToken = default);
}


public static class SampleDisks
{
    public static IReadOnlyList<DiskInfo> Create()
    {
        const long MiB = ByteSize.MiB, GiB = ByteSize.GiB;
        var sys = new DiskInfo
        {
            Number = 0, Model = "Sample NVMe SSD 512GB", SizeBytes = 512110190592, BusType = "NVMe", Media = MediaKind.Ssd,
            Style = PartitionStyle.Gpt, IsBoot = true, IsSystem = true, Health = HealthState.Healthy, SerialNumber = "SAMPLE-0001",
        };
        sys = sys.WithPartitions(
        [
            new() { DiskNumber = 0, PartitionNumber = 1, OffsetBytes = 1 * MiB, SizeBytes = 260 * MiB, Role = PartitionRole.EfiSystem, GptType = PartitionTypes.EfiSystem, FileSystem = "FAT32", IsSystem = true, IsHidden = true, VolumeSizeBytes = 260 * MiB, FreeBytes = 180 * MiB, BitLocker = BitLockerState.NotEncrypted },
            new() { DiskNumber = 0, PartitionNumber = 2, OffsetBytes = 261 * MiB, SizeBytes = 16 * MiB, Role = PartitionRole.MicrosoftReserved, GptType = PartitionTypes.MicrosoftReserved, IsHidden = true },
            new() { DiskNumber = 0, PartitionNumber = 3, OffsetBytes = 277 * MiB, SizeBytes = 150 * GiB, Role = PartitionRole.BasicData, GptType = PartitionTypes.BasicData, FileSystem = "NTFS", Label = "OS", DriveLetter = 'C', IsBoot = true, VolumeSizeBytes = 150 * GiB, FreeBytes = 51 * GiB, BitLocker = BitLockerState.NotEncrypted },
            new() { DiskNumber = 0, PartitionNumber = 4, OffsetBytes = 277 * MiB + 150 * GiB, SizeBytes = 1013 * MiB, Role = PartitionRole.Recovery, GptType = PartitionTypes.WindowsRecovery, FileSystem = "NTFS", IsHidden = true, VolumeSizeBytes = 1013 * MiB, FreeBytes = 72 * MiB },
            new() { DiskNumber = 0, PartitionNumber = 5, OffsetBytes = 1290 * MiB + 150 * GiB, SizeBytes = 320 * GiB, Role = PartitionRole.BasicData, GptType = PartitionTypes.BasicData, FileSystem = "NTFS", Label = "Data", DriveLetter = 'D', VolumeSizeBytes = 320 * GiB, FreeBytes = 190 * GiB, BitLocker = BitLockerState.NotEncrypted },
        ]);

        var usb = new DiskInfo
        {
            Number = 1, Model = "Sample USB Flash Drive", SizeBytes = 32 * GiB, BusType = "USB", Media = MediaKind.Unknown,
            Style = PartitionStyle.Mbr, Health = HealthState.Healthy, SerialNumber = "SAMPLE-0002",
        };
        usb = usb.WithPartitions(
        [
            new() { DiskNumber = 1, PartitionNumber = 1, OffsetBytes = 1 * MiB, SizeBytes = 16 * GiB, Role = PartitionRole.BasicData, MbrType = 0x07, FileSystem = "exFAT", Label = "USB", DriveLetter = 'E', IsActive = true, VolumeSizeBytes = 16 * GiB, FreeBytes = 12 * GiB, BitLocker = BitLockerState.NotEncrypted },
        ]);

        var blank = new DiskInfo
        {
            Number = 2, Model = "Sample Virtual Disk", SizeBytes = 64 * GiB, BusType = "File Backed Virtual", Media = MediaKind.Unknown,
            Style = PartitionStyle.Raw, IsVirtual = true, Health = HealthState.Healthy,
        };
        return [sys, usb, blank];
    }
}
