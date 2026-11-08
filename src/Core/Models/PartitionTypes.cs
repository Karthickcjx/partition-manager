namespace PartitionManager.Core.Models;

/// <summary>Well-known GPT type GUIDs and MBR type bytes (UEFI spec / Microsoft documentation).</summary>
public static class PartitionTypes
{
    public static readonly Guid EfiSystem = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid MicrosoftReserved = new("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    public static readonly Guid BasicData = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    public static readonly Guid WindowsRecovery = new("de94bba4-06d1-4d40-a16a-bfd50179d6ac");
    public static readonly Guid LdmMetadata = new("5808c8aa-7e8f-42e0-85d2-e1e90434cfb3");
    public static readonly Guid LdmData = new("af9b60a0-1431-4f62-bc68-3311714a69ad");
    public static readonly Guid LinuxFilesystem = new("0fc63daf-8483-4772-8e79-3d69d8477de4");
    public static readonly Guid LinuxSwap = new("0657fd6d-a4ab-43c4-84e5-0933c84b4f4f");

    public const byte MbrEmpty = 0x00;
    public const byte MbrFat32Lba = 0x0C;
    public const byte MbrNtfsExfat = 0x07;
    public const byte MbrExtendedChs = 0x05;
    public const byte MbrExtendedLba = 0x0F;
    public const byte MbrRecovery = 0x27;
    public const byte MbrLinux = 0x83;
    public const byte MbrGptProtective = 0xEE;
    public const byte MbrEfiSystem = 0xEF;
    public const byte MbrLdm = 0x42;

    public static PartitionRole RoleFromGpt(Guid type)
    {
        if (type == EfiSystem) return PartitionRole.EfiSystem;
        if (type == MicrosoftReserved) return PartitionRole.MicrosoftReserved;
        if (type == BasicData) return PartitionRole.BasicData;
        if (type == WindowsRecovery) return PartitionRole.Recovery;
        if (type == LdmMetadata) return PartitionRole.LdmMetadata;
        if (type == LdmData) return PartitionRole.LdmData;
        if (type == LinuxFilesystem || type == LinuxSwap) return PartitionRole.Linux;
        return type == Guid.Empty ? PartitionRole.Unknown : PartitionRole.Other;
    }

    public static PartitionRole RoleFromMbr(byte type) => type switch
    {
        MbrExtendedChs or MbrExtendedLba => PartitionRole.Extended,
        MbrRecovery => PartitionRole.Recovery,
        MbrEfiSystem => PartitionRole.EfiSystem,
        MbrLdm => PartitionRole.LdmData,
        MbrLinux or 0x82 => PartitionRole.Linux,
        0x01 or 0x04 or 0x06 or 0x07 or 0x0B or 0x0C or 0x0E or 0x11 or 0x14 or 0x16 or 0x17 or 0x1B or 0x1C or 0x1E
            => PartitionRole.BasicData,
        MbrEmpty => PartitionRole.Unknown,
        _ => PartitionRole.Other,
    };

    /// <summary>MBR type bytes 0x11-0x1E are the "hidden" variants of FAT/NTFS types.</summary>
    public static bool IsHiddenMbrType(byte type) => type is 0x11 or 0x14 or 0x16 or 0x17 or 0x1B or 0x1C or 0x1E;

    public static string RoleDisplayName(PartitionRole role) => role switch
    {
        PartitionRole.BasicData => "Basic data partition",
        PartitionRole.EfiSystem => "EFI system partition",
        PartitionRole.MicrosoftReserved => "Microsoft reserved partition",
        PartitionRole.Recovery => "Recovery partition",
        PartitionRole.Extended => "Extended partition",
        PartitionRole.LdmMetadata => "LDM metadata",
        PartitionRole.LdmData => "Dynamic disk data",
        PartitionRole.Linux => "Linux partition",
        PartitionRole.Other => "Other partition",
        _ => "Unrecognized",
    };

    public static string TypeColumnText(DiskInfo disk, PartitionInfo p)
    {
        if (disk.Style == PartitionStyle.Mbr)
        {
            if (p.IsExtendedContainer) return "Extended";
            return p.IsLogical ? "Logical" : "Primary";
        }
        return p.Role switch
        {
            PartitionRole.EfiSystem => "EFI System",
            PartitionRole.MicrosoftReserved => "MSR",
            PartitionRole.Recovery => "Recovery",
            PartitionRole.BasicData => "Primary",
            _ => RoleDisplayName(p.Role),
        };
    }
}
