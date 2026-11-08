using System.Buffers.Binary;
using PartitionManager.Core.Models;

namespace PartitionManager.Core.PartitionTables;

public sealed record MbrEntry(int Index, bool IsActive, byte Type, uint StartLba, uint SectorCount)
{
    public bool IsEmpty => Type == PartitionTypes.MbrEmpty || SectorCount == 0;
    public bool IsExtended => Type is PartitionTypes.MbrExtendedChs or PartitionTypes.MbrExtendedLba;
    public ulong EndLbaExclusive => (ulong)StartLba + SectorCount;
}

public sealed record MbrTable(uint DiskSignature, IReadOnlyList<MbrEntry> Entries)
{
    public bool IsProtective => Entries.Any(e => !e.IsEmpty && e.Type == PartitionTypes.MbrGptProtective);
    public IEnumerable<MbrEntry> UsedEntries => Entries.Where(e => !e.IsEmpty);
}

/// <summary>Parses the classic 512-byte master boot record and EBR chains.</summary>
public static class MbrParser
{
    public const int SectorBytes = 512;
    private const int EntryTableOffset = 446;
    private const int EntrySize = 16;

    public static bool HasBootSignature(ReadOnlySpan<byte> sector) =>
        sector.Length >= SectorBytes && sector[510] == 0x55 && sector[511] == 0xAA;

    public static MbrTable Parse(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < SectorBytes)
            throw new PartitionTableException($"MBR sector must be at least {SectorBytes} bytes (got {sector.Length}).");
        if (!HasBootSignature(sector))
            throw new PartitionTableException("Missing 0x55AA boot signature; the disk is not initialized with an MBR.");

        var entries = new MbrEntry[4];
        for (var i = 0; i < 4; i++)
        {
            var e = sector.Slice(EntryTableOffset + i * EntrySize, EntrySize);
            var status = e[0];
            if (status != 0x00 && status != 0x80)
                throw new PartitionTableException($"MBR entry {i} has invalid status byte 0x{status:X2}.");
            entries[i] = new MbrEntry(
                i,
                status == 0x80,
                e[4],
                BinaryPrimitives.ReadUInt32LittleEndian(e[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(e[12..]));
        }

        if (entries.Count(e => e.IsActive) > 1)
            throw new PartitionTableException("More than one MBR partition is marked active.");

        return new MbrTable(BinaryPrimitives.ReadUInt32LittleEndian(sector[440..]), entries);
    }

    /// <summary>
    /// Walks the extended boot record chain. <paramref name="readSector"/> returns one sector at the given LBA.
    /// Returned entries carry absolute LBAs.
    /// </summary>
    public static IReadOnlyList<MbrEntry> ReadLogicalPartitions(MbrEntry extended, Func<ulong, byte[]> readSector, int maxLogical = 128)
    {
        if (!extended.IsExtended) throw new ArgumentException("Entry is not an extended partition.", nameof(extended));
        var result = new List<MbrEntry>();
        var visited = new HashSet<ulong>();
        ulong ebrLba = extended.StartLba;
        while (result.Count < maxLogical)
        {
            if (!visited.Add(ebrLba))
                throw new PartitionTableException($"EBR chain loops back to LBA {ebrLba}.");
            var ebr = Parse(readSector(ebrLba));
            var data = ebr.Entries[0];
            if (!data.IsEmpty)
            {
                var absStart = ebrLba + data.StartLba;
                if (absStart + data.SectorCount > extended.EndLbaExclusive)
                    throw new PartitionTableException($"Logical partition at LBA {absStart} extends past its extended container.");
                result.Add(data with { Index = 4 + result.Count, StartLba = checked((uint)absStart) });
            }
            var next = ebr.Entries[1];
            if (next.IsEmpty) break;
            ebrLba = extended.StartLba + (ulong)next.StartLba;
        }
        return result;
    }
}
