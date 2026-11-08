using System.Buffers.Binary;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Models;
using PartitionManager.Core.PartitionTables;

namespace PartitionManager.Core.Recovery;

public enum FoundConfidence { High, Medium }

public sealed record FoundPartition(long OffsetBytes, long SizeBytes, string FileSystem, FoundConfidence Confidence, string Evidence);

/// <summary>
/// Looks for file-system boot sectors (NTFS, FAT32, exFAT) inside unallocated space, which is where a deleted or
/// lost partition's contents remain. Read-only: it reports candidates and does not rewrite the partition table.
/// </summary>
public static class LostPartitionScanner
{
    public static IReadOnlyList<FoundPartition> ScanUnallocated(ISectorSource disk, DiskInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        List<(long Start, long End)> ranges = info.Style is PartitionStyle.Raw or PartitionStyle.Unknown
            ? new() { (0L, info.SizeBytes) }
            : DiskLayout.Segments(info, minGap: DiskLayout.Alignment).Where(s => s.IsUnallocated).Select(s => (s.OffsetBytes, s.EndBytes)).ToList();
        return Scan(disk, ranges, progress, ct);
    }

    /// <summary>Probes the first sector of every 1 MiB boundary in each range (Windows has aligned partitions to 1 MiB since Vista).</summary>
    public static IReadOnlyList<FoundPartition> Scan(ISectorSource disk, IReadOnlyList<(long Start, long End)> ranges, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var found = new List<FoundPartition>();
        var ss = disk.SectorSize;
        long total = ranges.Sum(r => Math.Max(0, r.End - r.Start)), done = 0;
        foreach (var (start, end) in ranges)
        {
            var offset = DiskLayout.AlignUp(start);
            while (offset + ss <= end)
            {
                ct.ThrowIfCancellationRequested();
                var sector = disk.ReadSectors((ulong)(offset / ss), 1);
                var hit = Identify(sector, offset, disk);
                if (hit is not null)
                {
                    found.Add(hit);
                    offset = DiskLayout.AlignUp(offset + Math.Max(hit.SizeBytes, DiskLayout.Alignment));
                }
                else
                {
                    offset += DiskLayout.Alignment;
                }
                progress?.Report(total == 0 ? 1 : Math.Min(1, (done + offset - start) / (double)total));
            }
            done += end - start;
        }
        progress?.Report(1);
        return found;
    }

    internal static FoundPartition? Identify(ReadOnlySpan<byte> s, long offset, ISectorSource disk)
    {
        if (s.Length < 512 || s[510] != 0x55 || s[511] != 0xAA) return null;

        if (NtfsBootSector.TryParse(s) is { } ntfs)
        {
            // NTFS keeps a backup boot sector in the sector after the last one counted, so the partition is one sector longer.
            var size = (ntfs.TotalSectors + 1) * ntfs.BytesPerSector;
            var mftOk = false;
            try
            {
                var mftOffset = offset + ntfs.MftLcn * ntfs.ClusterSize;
                if (mftOffset + disk.SectorSize <= disk.SizeBytes)
                    mftOk = disk.ReadSectors((ulong)(mftOffset / disk.SectorSize), 1).AsSpan(0, 4).SequenceEqual("FILE"u8);
            }
            catch (PartitionTableException) { }
            return new(offset, size, "NTFS", mftOk ? FoundConfidence.High : FoundConfidence.Medium,
                mftOk ? "Boot sector and $MFT record found" : "Boot sector found; $MFT not confirmed");
        }

        if (s.Slice(3, 8).SequenceEqual("EXFAT   "u8))
        {
            var sectors = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[72..]);
            var shift = s[108];
            if (shift is >= 9 and <= 12 && sectors > 0)
                return new(offset, sectors << shift, "exFAT", FoundConfidence.Medium, "exFAT boot sector found");
        }

        if (s.Slice(82, 8).SequenceEqual("FAT32   "u8))
        {
            int bps = BinaryPrimitives.ReadUInt16LittleEndian(s[11..]);
            var sectors = (long)BinaryPrimitives.ReadUInt32LittleEndian(s[32..]);
            if (bps is 512 or 1024 or 2048 or 4096 && sectors > 0)
                return new(offset, sectors * bps, "FAT32", FoundConfidence.Medium, "FAT32 boot sector found");
        }
        return null;
    }
}
