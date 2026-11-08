using System.Buffers.Binary;
using System.Text;
using PartitionManager.Core.PartitionTables;

namespace PartitionManager.Tests;

/// <summary>Builds in-memory disk images with hand-assembled partition tables, for parser tests.</summary>
internal static class TestImages
{
    public const int Sector = 512;

    public record GptPart(Guid Type, ulong FirstLba, ulong LastLba, string Name = "", ulong Attributes = 0);

    public static byte[] Mbr(params (byte Type, uint Start, uint Count, bool Active)[] entries)
    {
        var s = new byte[Sector];
        BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(440), 0xA1B2C3D4);
        for (var i = 0; i < entries.Length; i++)
        {
            var e = s.AsSpan(446 + i * 16, 16);
            e[0] = (byte)(entries[i].Active ? 0x80 : 0);
            e[4] = entries[i].Type;
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], entries[i].Start);
            BinaryPrimitives.WriteUInt32LittleEndian(e[12..], entries[i].Count);
        }
        s[510] = 0x55; s[511] = 0xAA;
        return s;
    }

    public static byte[] GptImage(ulong totalSectors, params GptPart[] parts) => GptImage(totalSectors, corruptPrimaryCrc: false, parts);

    public static byte[] GptImage(ulong totalSectors, bool corruptPrimaryCrc, params GptPart[] parts)
    {
        var image = new byte[totalSectors * Sector];
        var protective = Mbr((0xEE, 1, (uint)Math.Min(totalSectors - 1, uint.MaxValue), false));
        protective.CopyTo(image, 0);

        const int entries = 128, entrySize = 128;
        var array = new byte[entries * entrySize];
        for (var i = 0; i < parts.Length; i++)
        {
            var e = array.AsSpan(i * entrySize, entrySize);
            parts[i].Type.TryWriteBytes(e[..16]);
            Guid.NewGuid().TryWriteBytes(e.Slice(16, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(e[32..], parts[i].FirstLba);
            BinaryPrimitives.WriteUInt64LittleEndian(e[40..], parts[i].LastLba);
            BinaryPrimitives.WriteUInt64LittleEndian(e[48..], parts[i].Attributes);
            Encoding.Unicode.GetBytes(parts[i].Name).CopyTo(e.Slice(56, 72));
        }
        var arrayCrc = Crc32.Compute(array);
        var arraySectors = (ulong)(array.Length / Sector);
        var diskGuid = Guid.NewGuid();
        var last = totalSectors - 1;

        byte[] Header(ulong current, ulong backup, ulong entriesLba)
        {
            var h = new byte[Sector];
            "EFI PART"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(8), 0x00010000);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(12), 92);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(24), current);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(32), backup);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(40), 34);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(48), last - 33);
            diskGuid.TryWriteBytes(h.AsSpan(56, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(72), entriesLba);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(80), entries);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(84), entrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(88), arrayCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), Crc32.Compute(h.AsSpan(0, 92)));
            return h;
        }

        var primary = Header(1, last, 2);
        if (corruptPrimaryCrc) primary[16] ^= 0xFF;
        primary.CopyTo(image, Sector);
        array.CopyTo(image, 2 * Sector);
        var backupEntriesLba = last - arraySectors;
        array.CopyTo(image, (long)backupEntriesLba * Sector);
        Header(last, 1, backupEntriesLba).CopyTo(image, (long)last * Sector);
        return image;
    }
}
