using System.Buffers.Binary;
using System.Text;

namespace PartitionManager.Core.Recovery;

public sealed class NtfsFormatException(string message) : Exception(message);

/// <summary>NTFS boot sector (BIOS parameter block), per the publicly documented on-disk layout.</summary>
public sealed record NtfsBootSector(int BytesPerSector, int SectorsPerCluster, long TotalSectors, long MftLcn, long MftMirrorLcn, int MftRecordSize, ulong SerialNumber)
{
    public int ClusterSize => BytesPerSector * SectorsPerCluster;
    public long VolumeBytes => TotalSectors * BytesPerSector;
    public long ClusterCount => VolumeBytes / ClusterSize;

    public static NtfsBootSector? TryParse(ReadOnlySpan<byte> s)
    {
        if (s.Length < 512 || !s.Slice(3, 8).SequenceEqual("NTFS    "u8) || s[510] != 0x55 || s[511] != 0xAA) return null;
        int bps = BinaryPrimitives.ReadUInt16LittleEndian(s[11..]);
        if (bps is not (512 or 1024 or 2048 or 4096)) return null;
        var rawSpc = s[13];
        var spc = rawSpc <= 0x80 ? rawSpc : 1 << (256 - rawSpc);
        if (spc == 0 || (spc & (spc - 1)) != 0 || spc > 65536) return null;
        var total = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[40..]);
        var mft = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[48..]);
        var mirror = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[56..]);
        var rawRecord = (sbyte)s[64];
        long recordSize = rawRecord > 0 ? (long)rawRecord * bps * spc : 1L << -rawRecord;
        if (total <= 0 || mft <= 0 || recordSize is not (1024 or 2048 or 4096)) return null;
        var clusters = total * bps / (bps * spc);
        if (mft >= clusters || mirror >= clusters) return null;
        return new NtfsBootSector(bps, spc, total, mft, mirror, (int)recordSize, BinaryPrimitives.ReadUInt64LittleEndian(s[72..]));
    }
}

/// <summary>One extent of a non-resident attribute. <see cref="Lcn"/> is -1 for a sparse (unallocated) run.</summary>
public readonly record struct DataRun(long Lcn, long ClusterCount)
{
    public bool IsSparse => Lcn < 0;
}

public static class DataRuns
{
    /// <summary>Decodes an NTFS mapping-pairs array. Throws <see cref="NtfsFormatException"/> on malformed input.</summary>
    public static List<DataRun> Decode(ReadOnlySpan<byte> runs, long maxClusters = long.MaxValue)
    {
        var list = new List<DataRun>();
        long lcn = 0;
        var i = 0;
        while (i < runs.Length && runs[i] != 0)
        {
            var header = runs[i++];
            var lengthBytes = header & 0x0F;
            var offsetBytes = header >> 4;
            if (lengthBytes is 0 or > 8 || offsetBytes > 8 || i + lengthBytes + offsetBytes > runs.Length)
                throw new NtfsFormatException("Malformed data run header.");
            long length = 0;
            for (var b = 0; b < lengthBytes; b++) length |= (long)runs[i + b] << (8 * b);
            i += lengthBytes;
            if (length <= 0 || length > maxClusters) throw new NtfsFormatException("Data run length is out of range.");
            if (offsetBytes == 0)
            {
                list.Add(new DataRun(-1, length));
                continue;
            }
            long delta = 0;
            for (var b = 0; b < offsetBytes; b++) delta |= (long)runs[i + b] << (8 * b);
            if ((runs[i + offsetBytes - 1] & 0x80) != 0 && offsetBytes < 8) delta |= -1L << (8 * offsetBytes); // sign-extend
            i += offsetBytes;
            lcn += delta;
            if (lcn < 0 || lcn > maxClusters || lcn + length > maxClusters) throw new NtfsFormatException("Data run points outside the volume.");
            list.Add(new DataRun(lcn, length));
        }
        return list;
    }
}

public sealed record NtfsFileName(long ParentRecord, ushort ParentSequence, string Name, byte Namespace, long RealSize, DateTime Modified);

public sealed record NtfsData(bool IsResident, byte[] ResidentBytes, IReadOnlyList<DataRun> Runs, long RealSize, long InitializedSize);

public sealed record MftRecord(
    long Number,
    ushort Sequence,
    bool InUse,
    bool IsDirectory,
    long BaseRecord,
    IReadOnlyList<NtfsFileName> FileNames,
    NtfsData? Data,
    bool HasAttributeList,
    DateTime? Created,
    DateTime? Modified)
{
    /// <summary>The name to show: Win32 or Win32+DOS namespace preferred over the 8.3 DOS name.</summary>
    public NtfsFileName? BestName => FileNames.OrderBy(n => n.Namespace switch { 1 => 0, 3 => 1, 0 => 2, _ => 3 }).FirstOrDefault();
}

/// <summary>Parses MFT FILE records defensively: every offset is bounds-checked, torn records are rejected.</summary>
public static class MftRecordParser
{
    public const uint AttrStandardInformation = 0x10, AttrAttributeList = 0x20, AttrFileName = 0x30, AttrData = 0x80, AttrEnd = 0xFFFFFFFF;

    /// <summary>Applies the update-sequence fixups in place. Returns false if the record is torn or malformed.</summary>
    public static bool ApplyFixups(Span<byte> record, int bytesPerSector)
    {
        if (record.Length < 48) return false;
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length || (usaCount - 1) * bytesPerSector > record.Length) return false;
        var usn = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (var i = 1; i < usaCount; i++)
        {
            var pos = i * bytesPerSector - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[pos..]) != usn) return false;
            record[pos] = record[usaOffset + i * 2];
            record[pos + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    /// <summary>Parses one record (fixups are applied to a copy). Returns null for empty, torn or corrupt records.</summary>
    public static MftRecord? Parse(ReadOnlySpan<byte> raw, int bytesPerSector, long number, long maxClusters)
    {
        if (raw.Length < 48 || !raw[..4].SequenceEqual("FILE"u8)) return null;
        var rec = raw.ToArray();
        if (!ApplyFixups(rec, bytesPerSector)) return null;
        try
        {
            var span = rec.AsSpan();
            var sequence = BinaryPrimitives.ReadUInt16LittleEndian(span[16..]);
            int firstAttr = BinaryPrimitives.ReadUInt16LittleEndian(span[20..]);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(span[22..]);
            var used = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(span[24..]), (uint)rec.Length);
            var baseRecord = (long)(BinaryPrimitives.ReadUInt64LittleEndian(span[32..]) & 0x0000FFFFFFFFFFFF);

            var names = new List<NtfsFileName>();
            NtfsData? data = null;
            var hasList = false;
            DateTime? created = null, modified = null;

            var pos = firstAttr;
            for (var guard = 0; guard < 256 && pos + 16 <= used; guard++)
            {
                var type = BinaryPrimitives.ReadUInt32LittleEndian(span[pos..]);
                if (type == AttrEnd) break;
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[(pos + 4)..]);
                if (length < 16 || pos + length > used) return null;
                var attr = span.Slice(pos, length);
                var nonResident = attr[8] != 0;
                var nameLength = attr[9];

                if (!nonResident)
                {
                    var valueLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(attr[16..]);
                    int valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[20..]);
                    if (valueOffset + valueLength > length) return null;
                    var value = attr.Slice(valueOffset, valueLength);
                    switch (type)
                    {
                        case AttrStandardInformation when value.Length >= 16:
                            created = FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(value));
                            modified = FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(value[8..]));
                            break;
                        case AttrFileName when value.Length >= 66:
                            int chars = value[64];
                            if (66 + chars * 2 > value.Length) return null;
                            var parent = BinaryPrimitives.ReadUInt64LittleEndian(value);
                            names.Add(new NtfsFileName(
                                (long)(parent & 0x0000FFFFFFFFFFFF), (ushort)(parent >> 48),
                                Encoding.Unicode.GetString(value.Slice(66, chars * 2)), value[65],
                                (long)BinaryPrimitives.ReadUInt64LittleEndian(value[48..]),
                                FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(value[16..])) ?? DateTime.MinValue));
                            break;
                        case AttrAttributeList:
                            hasList = true;
                            break;
                        case AttrData when nameLength == 0:
                            data = new NtfsData(true, value.ToArray(), [], value.Length, value.Length);
                            break;
                    }
                }
                else
                {
                    if (type == AttrAttributeList) hasList = true;
                    if (type == AttrData && nameLength == 0 && length >= 64)
                    {
                        var startVcn = BinaryPrimitives.ReadInt64LittleEndian(attr[16..]);
                        int runOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[32..]);
                        if (runOffset >= length) return null;
                        var realSize = BinaryPrimitives.ReadInt64LittleEndian(attr[48..]);
                        var initialized = BinaryPrimitives.ReadInt64LittleEndian(attr[56..]);
                        if (realSize < 0 || initialized < 0) return null;
                        // Only the first extent (VCN 0) is described here; later extents live in other records via an attribute list.
                        if (startVcn == 0)
                            data = new NtfsData(false, [], DataRuns.Decode(attr[runOffset..], maxClusters), realSize, Math.Min(initialized, realSize));
                        else
                            hasList = true;
                    }
                }
                pos += length;
            }
            return new MftRecord(number, sequence, (flags & 0x01) != 0, (flags & 0x02) != 0, baseRecord, names, data, hasList, created, modified);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or NtfsFormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTime? FromFileTime(long ft)
    {
        if (ft <= 0 || ft > DateTime.MaxValue.ToFileTimeUtc()) return null;
        return DateTime.FromFileTimeUtc(ft);
    }
}
