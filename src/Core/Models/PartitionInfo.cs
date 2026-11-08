namespace PartitionManager.Core.Models;

public enum PartitionRole
{
    Unknown,
    BasicData,
    EfiSystem,
    MicrosoftReserved,
    Recovery,
    Extended,
    LdmMetadata,
    LdmData,
    Linux,
    Other,
}

public enum BitLockerState { Unknown, NotEncrypted, Encrypted, Locked }

/// <summary>Immutable snapshot of one partition plus the volume (if any) mounted on it.</summary>
public sealed record PartitionInfo
{
    public required int DiskNumber { get; init; }
    public required int PartitionNumber { get; init; }
    public required long OffsetBytes { get; init; }
    public required long SizeBytes { get; init; }

    public PartitionRole Role { get; init; } = PartitionRole.Unknown;
    public Guid? GptType { get; init; }
    public byte? MbrType { get; init; }
    public bool IsLogical { get; init; }
    public bool IsActive { get; init; }
    public bool IsHidden { get; init; }
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }

    public char? DriveLetter { get; init; }
    public string Label { get; init; } = "";
    public string FileSystem { get; init; } = "";
    public long? VolumeSizeBytes { get; init; }
    public long? FreeBytes { get; init; }
    public BitLockerState BitLocker { get; init; } = BitLockerState.Unknown;
    public string VolumePath { get; init; } = "";

    /// <summary>GPT unique partition GUID (MSFT_Partition.Guid). Null on MBR disks.</summary>
    public Guid? UniqueGuid { get; init; }

    public long EndBytes => OffsetBytes + SizeBytes;

    public bool IsExtendedContainer => Role == PartitionRole.Extended;

    public long? UsedBytes =>
        FreeBytes is { } free ? Math.Max(0, (VolumeSizeBytes ?? SizeBytes) - free) : null;

    public double? UsedFraction =>
        UsedBytes is { } used && SizeBytes > 0 ? Math.Clamp((double)used / SizeBytes, 0, 1) : null;

    public bool HasFileSystem => !string.IsNullOrWhiteSpace(FileSystem);

    public bool IsProtectedRole =>
        Role is PartitionRole.EfiSystem or PartitionRole.MicrosoftReserved or PartitionRole.Recovery
             or PartitionRole.LdmMetadata or PartitionRole.LdmData;

    public bool IsEncrypted => BitLocker is BitLockerState.Encrypted or BitLockerState.Locked;

    public string DisplayName
    {
        get
        {
            var letter = DriveLetter is { } c ? $"{c}:" : null;
            var label = string.IsNullOrWhiteSpace(Label) ? null : Label;
            return (letter, label) switch
            {
                (not null, not null) => $"{label} ({letter})",
                (not null, null) => $"Local Disk ({letter})",
                (null, not null) => label!,
                _ => PartitionTypes.RoleDisplayName(Role),
            };
        }
    }
}
