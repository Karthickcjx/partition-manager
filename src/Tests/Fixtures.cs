using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;

namespace PartitionManager.Tests;

internal static class Fixtures
{
    public const long MiB = ByteSize.MiB;
    public const long GiB = ByteSize.GiB;

    public static PartitionInfo Part(int disk, int number, long offset, long size, string fs = "NTFS", char? letter = null,
        long? free = null, PartitionRole role = PartitionRole.BasicData, bool boot = false, bool system = false,
        BitLockerState bitlocker = BitLockerState.NotEncrypted, string label = "") => new()
    {
        DiskNumber = disk, PartitionNumber = number, OffsetBytes = offset, SizeBytes = size, FileSystem = fs, DriveLetter = letter,
        VolumeSizeBytes = string.IsNullOrEmpty(fs) ? null : size, FreeBytes = string.IsNullOrEmpty(fs) ? null : free ?? size,
        Role = role, IsBoot = boot, IsSystem = system, BitLocker = bitlocker, Label = label,
    };

    /// <summary>64 GiB GPT data disk: [1 MiB gap][P1 10 GiB E:][P2 20 GiB F:][free ~34 GiB].</summary>
    public static DiskInfo DataDisk(int number = 1)
    {
        var d = new DiskInfo { Number = number, Model = "Test Disk", SizeBytes = 64 * GiB, Style = PartitionStyle.Gpt, Health = HealthState.Healthy };
        return d.WithPartitions(
        [
            Part(number, 1, 1 * MiB, 10 * GiB, letter: 'E', free: 8 * GiB, label: "Data1"),
            Part(number, 2, 1 * MiB + 10 * GiB, 20 * GiB, letter: 'F', free: 15 * GiB, label: "Data2"),
        ]);
    }

    /// <summary>System disk: ESP, MSR, C: (boot), recovery.</summary>
    public static DiskInfo SystemDisk()
    {
        var d = new DiskInfo { Number = 0, Model = "System SSD", SizeBytes = 256 * GiB, Style = PartitionStyle.Gpt, IsBoot = true, IsSystem = true, Health = HealthState.Healthy };
        return d.WithPartitions(
        [
            Part(0, 1, 1 * MiB, 100 * MiB, "FAT32", role: PartitionRole.EfiSystem, system: true),
            Part(0, 2, 101 * MiB, 16 * MiB, "", role: PartitionRole.MicrosoftReserved),
            Part(0, 3, 117 * MiB, 200 * GiB, letter: 'C', free: 80 * GiB, boot: true),
            Part(0, 4, 117 * MiB + 200 * GiB, 1 * GiB, role: PartitionRole.Recovery),
        ]);
    }

    public static PreviewState State(params DiskInfo[] disks) => new(disks);
}
