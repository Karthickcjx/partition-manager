using PartitionManager.Core.Models;

namespace PartitionManager.Core.PartitionTables;

/// <summary>Read-only access to raw sectors of a disk or disk image.</summary>
public interface ISectorSource
{
    int SectorSize { get; }
    long SizeBytes { get; }
    byte[] ReadSectors(ulong lba, int count);
}

/// <summary>A sector source over any seekable stream (disk image files, MemoryStream in tests).</summary>
public sealed class StreamSectorSource(Stream stream, int sectorSize = 512) : ISectorSource
{
    public int SectorSize { get; } = sectorSize;
    public long SizeBytes => stream.Length;

    public byte[] ReadSectors(ulong lba, int count)
    {
        var buffer = new byte[SectorSize * count];
        stream.Position = checked((long)lba * SectorSize);
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0) throw new PartitionTableException($"Unexpected end of disk while reading LBA {lba}.");
            read += n;
        }
        return buffer;
    }
}

public sealed record RawPartition(int Number, ulong StartLba, ulong SectorCount, PartitionRole Role, Guid? GptType, byte? MbrType,
    bool IsActive, bool IsLogical, bool IsHidden, string Name);

public sealed record PartitionTableSnapshot(PartitionStyle Style, int SectorSize, IReadOnlyList<RawPartition> Partitions,
    GptHeader? Gpt, bool GptEntriesCrcValid, bool BackupGptValid, MbrTable? Mbr, IReadOnlyList<string> Warnings);

public static class PartitionTableReader
{
    public static PartitionTableSnapshot Read(ISectorSource source)
    {
        var warnings = new List<string>();
        var ss = source.SectorSize;
        var lba0 = source.ReadSectors(0, 1);
        if (!MbrParser.HasBootSignature(lba0))
            return new PartitionTableSnapshot(PartitionStyle.Raw, ss, [], null, false, false, null, ["Disk has no partition table (uninitialized)."]);

        var mbr = MbrParser.Parse(lba0);
        if (mbr.IsProtective)
            return ReadGpt(source, mbr, warnings);

        var parts = new List<RawPartition>();
        var n = 1;
        foreach (var e in mbr.UsedEntries)
        {
            parts.Add(new RawPartition(n++, e.StartLba, e.SectorCount, PartitionTypes.RoleFromMbr(e.Type), null, e.Type,
                e.IsActive, false, PartitionTypes.IsHiddenMbrType(e.Type), ""));
            if (e.IsExtended)
            {
                foreach (var l in MbrParser.ReadLogicalPartitions(e, lba => source.ReadSectors(lba, 1)))
                    parts.Add(new RawPartition(n++, l.StartLba, l.SectorCount, PartitionTypes.RoleFromMbr(l.Type), null, l.Type,
                        l.IsActive, true, PartitionTypes.IsHiddenMbrType(l.Type), ""));
            }
        }
        return new PartitionTableSnapshot(PartitionStyle.Mbr, ss, parts, null, false, false, mbr, warnings);
    }

    private static PartitionTableSnapshot ReadGpt(ISectorSource source, MbrTable mbr, List<string> warnings)
    {
        var ss = source.SectorSize;
        var header = GptParser.ParseHeader(source.ReadSectors(1, 1));
        if (!header.HeaderCrcValid) warnings.Add("Primary GPT header CRC32 mismatch.");

        var entrySectors = (int)((GptParser.EntryArrayBytes(header) + ss - 1) / ss);
        var entries = GptParser.ParseEntries(source.ReadSectors(header.PartitionEntriesLba, entrySectors), header, out var entriesOk);
        if (!entriesOk) warnings.Add("Primary GPT partition entry array CRC32 mismatch.");

        var backupOk = false;
        var lastLba = (ulong)(source.SizeBytes / ss) - 1;
        if (header.BackupLba != lastLba)
            warnings.Add($"Backup GPT header is expected at LBA {lastLba} but the primary header points to {header.BackupLba}.");
        try
        {
            if (header.BackupLba <= lastLba)
            {
                var backup = GptParser.ParseHeader(source.ReadSectors(header.BackupLba, 1));
                backupOk = backup.HeaderCrcValid && backup.DiskGuid == header.DiskGuid;
                if (!backupOk) warnings.Add("Backup GPT header is damaged or does not match the primary header.");
            }
        }
        catch (PartitionTableException ex)
        {
            warnings.Add("Backup GPT header unreadable: " + ex.Message);
        }

        var parts = entries
            .OrderBy(e => e.FirstLba)
            .Select((e, i) => new RawPartition(i + 1, e.FirstLba, e.SectorCount, PartitionTypes.RoleFromGpt(e.TypeGuid), e.TypeGuid, null,
                false, false, e.IsHidden || e.NoDriveLetter, e.Name))
            .ToList();

        foreach (var p in parts)
        {
            if (p.StartLba < header.FirstUsableLba || p.StartLba + p.SectorCount - 1 > header.LastUsableLba)
                warnings.Add($"Partition {p.Number} lies outside the usable LBA range.");
        }
        return new PartitionTableSnapshot(PartitionStyle.Gpt, ss, parts, header, entriesOk, backupOk, mbr, warnings);
    }
}
