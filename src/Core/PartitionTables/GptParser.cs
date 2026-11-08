using System.Buffers.Binary;
using System.Text;

namespace PartitionManager.Core.PartitionTables;

public sealed record GptHeader(
    uint Revision,
    uint HeaderSize,
    uint HeaderCrc32,
    bool HeaderCrcValid,
    ulong CurrentLba,
    ulong BackupLba,
    ulong FirstUsableLba,
    ulong LastUsableLba,
    Guid DiskGuid,
    ulong PartitionEntriesLba,
    uint NumberOfEntries,
    uint EntrySize,
    uint EntriesCrc32);

public sealed record GptEntry(int Index, Guid TypeGuid, Guid UniqueGuid, ulong FirstLba, ulong LastLba, ulong Attributes, string Name)
{
    public const ulong AttrRequired = 1UL << 0;
    public const ulong AttrNoBlockIo = 1UL << 1;
    public const ulong AttrHidden = 1UL << 62;
    public const ulong AttrNoDriveLetter = 1UL << 63;

    public bool IsEmpty => TypeGuid == Guid.Empty;
    public ulong SectorCount => LastLba >= FirstLba ? LastLba - FirstLba + 1 : 0;
    public bool IsHidden => (Attributes & AttrHidden) != 0;
    public bool NoDriveLetter => (Attributes & AttrNoDriveLetter) != 0;
}

/// <summary>Parses GUID partition table headers and entry arrays per UEFI 2.x, section 5.3.</summary>
public static class GptParser
{
    public static ReadOnlySpan<byte> Signature => "EFI PART"u8;
    public const int MinHeaderSize = 92;

    public static GptHeader ParseHeader(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < MinHeaderSize)
            throw new PartitionTableException("GPT header sector is too short.");
        if (!sector[..8].SequenceEqual(Signature))
            throw new PartitionTableException("GPT header signature 'EFI PART' not found.");

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(sector[12..]);
        if (headerSize < MinHeaderSize || headerSize > sector.Length)
            throw new PartitionTableException($"GPT header size {headerSize} is invalid.");

        var storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(sector[16..]);
        var copy = sector[..(int)headerSize].ToArray();
        copy.AsSpan(16, 4).Clear();
        var crcValid = Crc32.Compute(copy) == storedCrc;

        var header = new GptHeader(
            Revision: BinaryPrimitives.ReadUInt32LittleEndian(sector[8..]),
            HeaderSize: headerSize,
            HeaderCrc32: storedCrc,
            HeaderCrcValid: crcValid,
            CurrentLba: BinaryPrimitives.ReadUInt64LittleEndian(sector[24..]),
            BackupLba: BinaryPrimitives.ReadUInt64LittleEndian(sector[32..]),
            FirstUsableLba: BinaryPrimitives.ReadUInt64LittleEndian(sector[40..]),
            LastUsableLba: BinaryPrimitives.ReadUInt64LittleEndian(sector[48..]),
            DiskGuid: new Guid(sector.Slice(56, 16)),
            PartitionEntriesLba: BinaryPrimitives.ReadUInt64LittleEndian(sector[72..]),
            NumberOfEntries: BinaryPrimitives.ReadUInt32LittleEndian(sector[80..]),
            EntrySize: BinaryPrimitives.ReadUInt32LittleEndian(sector[84..]),
            EntriesCrc32: BinaryPrimitives.ReadUInt32LittleEndian(sector[88..]));

        if (header.EntrySize < 128 || header.EntrySize % 8 != 0)
            throw new PartitionTableException($"GPT entry size {header.EntrySize} is invalid.");
        if (header.NumberOfEntries > 16384)
            throw new PartitionTableException($"GPT claims {header.NumberOfEntries} entries, which is not plausible.");
        if (header.FirstUsableLba > header.LastUsableLba)
            throw new PartitionTableException("GPT first usable LBA is after last usable LBA.");
        return header;
    }

    public static long EntryArrayBytes(GptHeader header) => (long)header.NumberOfEntries * header.EntrySize;

    public static IReadOnlyList<GptEntry> ParseEntries(ReadOnlySpan<byte> entryArray, GptHeader header, out bool entriesCrcValid)
    {
        var total = EntryArrayBytes(header);
        if (entryArray.Length < total)
            throw new PartitionTableException($"GPT entry array is truncated ({entryArray.Length} of {total} bytes).");

        var array = entryArray[..(int)total];
        entriesCrcValid = Crc32.Compute(array) == header.EntriesCrc32;

        var list = new List<GptEntry>();
        for (var i = 0; i < header.NumberOfEntries; i++)
        {
            var e = array.Slice(i * (int)header.EntrySize, (int)header.EntrySize);
            var type = new Guid(e[..16]);
            if (type == Guid.Empty) continue;
            var nameBytes = e.Slice(56, 72);
            var name = Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
            var entry = new GptEntry(
                i,
                type,
                new Guid(e.Slice(16, 16)),
                BinaryPrimitives.ReadUInt64LittleEndian(e[32..]),
                BinaryPrimitives.ReadUInt64LittleEndian(e[40..]),
                BinaryPrimitives.ReadUInt64LittleEndian(e[48..]),
                name);
            if (entry.LastLba < entry.FirstLba)
                throw new PartitionTableException($"GPT entry {i} ends before it starts.");
            list.Add(entry);
        }
        return list;
    }
}
