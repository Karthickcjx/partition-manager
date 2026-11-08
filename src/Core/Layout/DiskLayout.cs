using PartitionManager.Core.Models;

namespace PartitionManager.Core.Layout;

/// <summary>One contiguous region on a disk map: either a partition or unallocated space.</summary>
public sealed record LayoutSegment(int DiskNumber, long OffsetBytes, long SizeBytes, PartitionInfo? Partition)
{
    public bool IsUnallocated => Partition is null;
    public long EndBytes => OffsetBytes + SizeBytes;
}

public static class DiskLayout
{
    /// <summary>Windows aligns new partitions to 1 MiB since Vista; we use the same rule.</summary>
    public const long Alignment = ByteSize.MiB;

    /// <summary>
    /// Gaps smaller than this are alignment padding (Windows commonly leaves exactly 1 MiB between partitions), not
    /// usable free space, and are neither displayed nor offered for new partitions.
    /// </summary>
    public const long MinimumUsableGap = 8 * ByteSize.MiB;

    public static long AlignUp(long value, long alignment = Alignment) =>
        value % alignment == 0 ? value : checked(value + alignment - value % alignment);

    public static long AlignDown(long value, long alignment = Alignment) => value - value % alignment;

    public static long UsableStart(DiskInfo disk) => Alignment;

    /// <summary>GPT keeps a 33-sector backup table at the end of the disk.</summary>
    public static long UsableEnd(DiskInfo disk) => disk.Style switch
    {
        PartitionStyle.Gpt => disk.SizeBytes - 33L * disk.LogicalSectorSize,
        _ => disk.SizeBytes,
    };

    /// <summary>Partitions that occupy disk-map space (extended containers are shown via their logicals).</summary>
    public static IEnumerable<PartitionInfo> MapPartitions(DiskInfo disk) =>
        disk.Partitions.Where(p => !p.IsExtendedContainer).OrderBy(p => p.OffsetBytes);

    public static IReadOnlyList<LayoutSegment> Segments(DiskInfo disk, long minGap = MinimumUsableGap)
    {
        var segments = new List<LayoutSegment>();
        if (disk.Style is PartitionStyle.Raw or PartitionStyle.Unknown && disk.Partitions.Count == 0)
        {
            segments.Add(new LayoutSegment(disk.Number, 0, disk.SizeBytes, null));
            return segments;
        }

        var cursor = UsableStart(disk);
        foreach (var p in MapPartitions(disk))
        {
            if (p.OffsetBytes - cursor >= minGap)
                segments.Add(new LayoutSegment(disk.Number, cursor, p.OffsetBytes - cursor, null));
            segments.Add(new LayoutSegment(disk.Number, p.OffsetBytes, p.SizeBytes, p));
            cursor = Math.Max(cursor, p.EndBytes);
        }
        var end = UsableEnd(disk);
        if (end - cursor >= minGap)
            segments.Add(new LayoutSegment(disk.Number, cursor, end - cursor, null));
        return segments;
    }

    public static IReadOnlyList<LayoutSegment> FreeSegments(DiskInfo disk) =>
        Segments(disk).Where(s => s.IsUnallocated).ToList();

    public static long UnallocatedBytes(DiskInfo disk) => FreeSegments(disk).Sum(s => s.SizeBytes);

    /// <summary>Bounds a partition can grow into: from the end of the previous neighbour to the start of the next.</summary>
    public static (long Min, long Max) MovableRange(DiskInfo disk, PartitionInfo partition)
    {
        var others = MapPartitions(disk).Where(p => p.PartitionNumber != partition.PartitionNumber).ToList();
        var prevEnd = others.Where(p => p.EndBytes <= partition.OffsetBytes).Select(p => p.EndBytes).DefaultIfEmpty(UsableStart(disk)).Max();
        var nextStart = others.Where(p => p.OffsetBytes >= partition.EndBytes).Select(p => p.OffsetBytes).DefaultIfEmpty(UsableEnd(disk)).Min();
        return (AlignUp(prevEnd), nextStart);
    }

    public static PartitionInfo? NeighbourBefore(DiskInfo disk, PartitionInfo p) =>
        MapPartitions(disk).Where(o => o.EndBytes <= p.OffsetBytes).OrderByDescending(o => o.EndBytes).FirstOrDefault();

    public static PartitionInfo? NeighbourAfter(DiskInfo disk, PartitionInfo p) =>
        MapPartitions(disk).Where(o => o.OffsetBytes >= p.EndBytes).OrderBy(o => o.OffsetBytes).FirstOrDefault();
}
