using PartitionManager.Core.Models;

namespace PartitionManager.Core.FileSystems;

/// <summary>
/// File-system capabilities as exposed by documented Windows tooling (format.com, Format-Volume, Resize-Partition).
/// </summary>
public static class FileSystemRules
{
    public const string Ntfs = "NTFS";
    public const string Fat32 = "FAT32";
    public const string ExFat = "exFAT";
    public const string ReFs = "ReFS";

    public static IReadOnlyList<string> Formattable { get; } = [Ntfs, Fat32, ExFat];

    /// <summary>Windows' built-in formatter refuses FAT32 volumes larger than 32 GB.</summary>
    public const long Fat32MaxFormatBytes = 32 * ByteSize.GiB;
    public const long Fat32MinBytes = 32 * ByteSize.MiB;
    public const long NtfsMinBytes = 8 * ByteSize.MiB;

    /// <summary>Headroom required above used space before shrinking, so the volume stays writable.</summary>
    public const long ShrinkHeadroomBytes = 32 * ByteSize.MiB;

    public static string Normalize(string? fs) => (fs ?? "").Trim().ToUpperInvariant() switch
    {
        "NTFS" => Ntfs,
        "FAT32" => Fat32,
        "EXFAT" => ExFat,
        "REFS" => ReFs,
        "FAT" or "FAT16" => "FAT",
        "" or "RAW" or "UNKNOWN" => "",
        var other => other,
    };

    public static bool IsFormattable(string fs) => Formattable.Contains(Normalize(fs));

    public static bool CanExtend(string fs) => Normalize(fs) is "" or Ntfs or ReFs;

    public static bool CanShrink(string fs) => Normalize(fs) is "" or Ntfs;

    /// <summary>File systems whose contents we know how to relocate (move, merge source).</summary>
    public static bool CanRelocateData(string fs) => Normalize(fs) is "" or Ntfs or Fat32 or ExFat;

    public static int MaxLabelLength(string fs) => Normalize(fs) switch
    {
        Fat32 or "FAT" => 11,
        ExFat => 15,
        _ => 32,
    };

    private static readonly char[] FatForbidden = ['*', '?', '/', '\\', '|', ',', ';', ':', '+', '=', '<', '>', '[', ']', '"', '.'];

    public static string? ValidateLabel(string fs, string? label)
    {
        label ??= "";
        var max = MaxLabelLength(fs);
        if (label.Length > max) return $"{Normalize(fs)} volume labels can be at most {max} characters.";
        var nfs = Normalize(fs);
        if ((nfs is Fat32 or "FAT" or ExFat) && label.IndexOfAny(FatForbidden) >= 0)
            return $"{nfs} volume labels cannot contain * ? / \\ | , ; : + = < > [ ] \" or .";
        if (label.Any(char.IsControl)) return "Volume labels cannot contain control characters.";
        return null;
    }

    public static string? ValidateSizeForFormat(string fs, long sizeBytes) => Normalize(fs) switch
    {
        Fat32 when sizeBytes > Fat32MaxFormatBytes => $"Windows can only format FAT32 volumes up to {ByteSize.Format(Fat32MaxFormatBytes)}. Choose NTFS or exFAT.",
        Fat32 when sizeBytes < Fat32MinBytes => $"FAT32 needs at least {ByteSize.Format(Fat32MinBytes)}.",
        Ntfs when sizeBytes < NtfsMinBytes => $"NTFS needs at least {ByteSize.Format(NtfsMinBytes)}.",
        "" => "Choose a file system.",
        var other when !IsFormattable(other) => $"Formatting as {other} is not supported.",
        _ => null,
    };

    public static IReadOnlyList<int> ClusterSizes(string fs) => Normalize(fs) switch
    {
        Ntfs => [0, 4096, 8192, 16384, 32768, 65536],
        Fat32 => [0, 4096, 8192, 16384, 32768],
        ExFat => [0, 4096, 32768, 131072],
        _ => [0],
    };

    public static byte MbrTypeFor(string fs) => Normalize(fs) == Fat32 ? PartitionTypes.MbrFat32Lba : PartitionTypes.MbrNtfsExfat;
}
