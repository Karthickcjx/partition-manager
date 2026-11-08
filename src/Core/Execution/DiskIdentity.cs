using System.Security.Cryptography;
using System.Text;
using PartitionManager.Core.Models;

namespace PartitionManager.Core.Execution;

/// <summary>
/// Everything needed to be sure a request targets the same physical (or virtual) disk, in the same layout, that the
/// user looked at. Disk numbers alone are not trusted: they change across reboots and device arrival.
/// </summary>
public sealed record DiskIdentity(
    int Number,
    string UniqueId,
    string SerialNumber,
    long SizeBytes,
    PartitionStyle Style,
    string LayoutFingerprint,
    string VirtualImagePath)
{
    public static DiskIdentity From(DiskInfo disk) => new(
        disk.Number, disk.UniqueId, disk.SerialNumber.Trim(), disk.SizeBytes, disk.Style, LayoutFingerprints.Compute(disk), disk.VirtualImagePath);

    public bool HasStableId => !string.IsNullOrWhiteSpace(UniqueId) || !string.IsNullOrWhiteSpace(SerialNumber);
}

/// <summary>A partition pinned by geometry (and GPT unique GUID where available), not just by its number.</summary>
public sealed record PartitionTarget(int PartitionNumber, long OffsetBytes, long SizeBytes, Guid? UniqueGuid)
{
    public static PartitionTarget From(PartitionInfo p) => new(p.PartitionNumber, p.OffsetBytes, p.SizeBytes, p.UniqueGuid);

    public bool Matches(PartitionInfo p) =>
        p.PartitionNumber == PartitionNumber && p.OffsetBytes == OffsetBytes && p.SizeBytes == SizeBytes && p.UniqueGuid == UniqueGuid;
}

public static class LayoutFingerprints
{
    /// <summary>
    /// SHA-256 over the partition geometry and types only. Labels, drive letters and free space are excluded so that
    /// metadata operations do not invalidate their own identity check, while any structural change does.
    /// </summary>
    public static string Compute(DiskInfo disk)
    {
        var sb = new StringBuilder();
        sb.Append(disk.Style).Append('|').Append(disk.SizeBytes).Append('|').Append(disk.LogicalSectorSize).Append(';');
        foreach (var p in disk.Partitions.OrderBy(p => p.OffsetBytes).ThenBy(p => p.PartitionNumber))
        {
            sb.Append(p.PartitionNumber).Append(':').Append(p.OffsetBytes).Append(':').Append(p.SizeBytes).Append(':')
              .Append(p.GptType?.ToString() ?? "").Append(':').Append(p.MbrType?.ToString() ?? "").Append(':')
              .Append(p.UniqueGuid?.ToString() ?? "").Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}

public enum IdentityStatus { Matched, NotFound, Ambiguous, InsufficientIdentity, DiskNumberChanged, SizeChanged, LayoutChanged }

public sealed record IdentityMatch(IdentityStatus Status, DiskInfo? Disk, string Message)
{
    public bool IsMatch => Status == IdentityStatus.Matched;
}

public static class DiskIdentityMatcher
{
    /// <summary>
    /// Finds the disk described by <paramref name="expected"/> among freshly enumerated disks. Any doubt is a refusal:
    /// missing stable IDs, more than one candidate, a renumbered disk or a changed layout all fail.
    /// </summary>
    public static IdentityMatch Match(DiskIdentity expected, IReadOnlyList<DiskInfo> current)
    {
        if (!expected.HasStableId)
            return new(IdentityStatus.InsufficientIdentity, null, $"Disk {expected.Number} reports neither a unique ID nor a serial number, so it cannot be identified safely.");

        var candidates = current.Where(d => IdsMatch(expected, d)).ToList();
        if (candidates.Count == 0)
            return new(IdentityStatus.NotFound, null, $"The target disk ({Describe(expected)}) is no longer connected.");
        if (candidates.Count > 1)
            return new(IdentityStatus.Ambiguous, null, $"{candidates.Count} connected disks share the identity {Describe(expected)}; refusing to guess.");

        var disk = candidates[0];
        if (disk.Number != expected.Number)
            return new(IdentityStatus.DiskNumberChanged, disk, $"The target disk is now Disk {disk.Number} instead of Disk {expected.Number}. Refresh and review the tasks again.");
        if (disk.SizeBytes != expected.SizeBytes)
            return new(IdentityStatus.SizeChanged, disk, "The target disk's capacity changed since it was inspected.");
        if (!string.Equals(LayoutFingerprints.Compute(disk), expected.LayoutFingerprint, StringComparison.Ordinal))
            return new(IdentityStatus.LayoutChanged, disk, "The partition layout of the target disk changed since it was inspected. Refresh and review the tasks again.");
        return new(IdentityStatus.Matched, disk, "");
    }

    private static bool IdsMatch(DiskIdentity e, DiskInfo d)
    {
        if (!string.IsNullOrWhiteSpace(e.UniqueId))
            return string.Equals(e.UniqueId, d.UniqueId, StringComparison.OrdinalIgnoreCase)
                   && (string.IsNullOrWhiteSpace(e.SerialNumber) || string.Equals(e.SerialNumber, d.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase));
        return string.Equals(e.SerialNumber, d.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase) && e.SizeBytes == d.SizeBytes;
    }

    private static string Describe(DiskIdentity e) =>
        string.IsNullOrWhiteSpace(e.SerialNumber) ? $"ID {e.UniqueId}" : $"S/N {e.SerialNumber}";
}
