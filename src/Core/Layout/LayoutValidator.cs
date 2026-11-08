using PartitionManager.Core.Models;

namespace PartitionManager.Core.Layout;

public enum IssueSeverity { Info, Warning, Error }

public sealed record LayoutIssue(IssueSeverity Severity, string Code, string Message, int? PartitionNumber = null);

/// <summary>Checks a disk layout for structural problems: bounds, overlaps, alignment and table limits.</summary>
public static class LayoutValidator
{
    public const long MbrMaxAddressableBytes = 0xFFFFFFFFL * 512;
    public const int GptMaxEntries = 128;

    public static IReadOnlyList<LayoutIssue> Validate(DiskInfo disk)
    {
        var issues = new List<LayoutIssue>();
        var ss = disk.LogicalSectorSize;

        foreach (var p in disk.Partitions)
        {
            if (p.SizeBytes <= 0)
                issues.Add(new(IssueSeverity.Error, "ZERO_SIZE", $"Partition {p.PartitionNumber} has no size.", p.PartitionNumber));
            if (p.OffsetBytes < 0)
                issues.Add(new(IssueSeverity.Error, "NEGATIVE_OFFSET", $"Partition {p.PartitionNumber} starts before the disk.", p.PartitionNumber));
            if (p.EndBytes > disk.SizeBytes)
                issues.Add(new(IssueSeverity.Error, "OUT_OF_BOUNDS", $"Partition {p.PartitionNumber} ends {ByteSize.Format(p.EndBytes - disk.SizeBytes)} past the end of the disk.", p.PartitionNumber));
            if (disk.Style == PartitionStyle.Gpt && p.EndBytes > DiskLayout.UsableEnd(disk))
                issues.Add(new(IssueSeverity.Error, "BACKUP_GPT_OVERLAP", $"Partition {p.PartitionNumber} overlaps the backup GPT area.", p.PartitionNumber));
            if (ss > 0 && (p.OffsetBytes % ss != 0 || p.SizeBytes % ss != 0))
                issues.Add(new(IssueSeverity.Error, "SECTOR_MISALIGNED", $"Partition {p.PartitionNumber} is not a whole number of {ss}-byte sectors.", p.PartitionNumber));
            else if (!p.IsExtendedContainer && !p.IsLogical && p.OffsetBytes % DiskLayout.Alignment != 0)
                issues.Add(new(IssueSeverity.Warning, "NOT_1MIB_ALIGNED", $"Partition {p.PartitionNumber} is not aligned to 1 MiB, which can slow SSDs and Advanced Format drives.", p.PartitionNumber));
        }

        var mapped = DiskLayout.MapPartitions(disk).ToList();
        for (var i = 1; i < mapped.Count; i++)
        {
            if (mapped[i].OffsetBytes < mapped[i - 1].EndBytes)
                issues.Add(new(IssueSeverity.Error, "OVERLAP",
                    $"Partitions {mapped[i - 1].PartitionNumber} and {mapped[i].PartitionNumber} overlap.", mapped[i].PartitionNumber));
        }

        if (disk.Style == PartitionStyle.Mbr)
        {
            var primaries = disk.Partitions.Count(p => !p.IsLogical);
            if (primaries > 4)
                issues.Add(new(IssueSeverity.Error, "MBR_TOO_MANY_PRIMARY", $"MBR disks support 4 primary/extended partitions; found {primaries}."));
            if (disk.Partitions.Count(p => p.IsExtendedContainer) > 1)
                issues.Add(new(IssueSeverity.Error, "MBR_MULTIPLE_EXTENDED", "An MBR disk can contain only one extended partition."));
            var ext = disk.Partitions.FirstOrDefault(p => p.IsExtendedContainer);
            foreach (var l in disk.Partitions.Where(p => p.IsLogical))
            {
                if (ext is null || l.OffsetBytes < ext.OffsetBytes || l.EndBytes > ext.EndBytes)
                    issues.Add(new(IssueSeverity.Error, "LOGICAL_OUTSIDE_EXTENDED", $"Logical partition {l.PartitionNumber} is not inside an extended partition.", l.PartitionNumber));
            }
            if (disk.SizeBytes > MbrMaxAddressableBytes)
                issues.Add(new(IssueSeverity.Warning, "MBR_OVER_2TB", $"MBR can only address the first 2 TB of this {ByteSize.Format(disk.SizeBytes)} disk. Convert to GPT to use all of it."));
            if (disk.Partitions.Any(p => p.EndBytes > MbrMaxAddressableBytes))
                issues.Add(new(IssueSeverity.Error, "MBR_ADDRESS_LIMIT", "A partition ends beyond the 2 TB MBR addressing limit."));
        }
        else if (disk.Style == PartitionStyle.Gpt && disk.Partitions.Count > GptMaxEntries)
        {
            issues.Add(new(IssueSeverity.Error, "GPT_TOO_MANY", $"The standard GPT entry array holds {GptMaxEntries} partitions; found {disk.Partitions.Count}."));
        }

        return issues;
    }

    public static bool IsValid(DiskInfo disk) => Validate(disk).All(i => i.Severity != IssueSeverity.Error);
}
