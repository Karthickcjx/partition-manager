using System.Buffers.Binary;
using System.Text;

namespace PartitionManager.Tests.Recovery;

/// <summary>
/// Hand-assembles a small NTFS volume image (4 KiB clusters, 1 KiB MFT records) containing just enough structure for
/// the recovery scanner: boot sector, $MFT, root directory, $Bitmap, folders and live/deleted files.
/// </summary>
internal sealed class NtfsTestImage
{
    public const int Bps = 512, Spc = 8, Cluster = Bps * Spc, RecordSize = 1024;
    public const long MftLcn = 16, MftClusters = 16; // 64 records
    public readonly byte[] Image;
    private readonly byte[] _bitmap;
    public long TotalClusters { get; }

    public NtfsTestImage(int totalClusters = 4096)
    {
        TotalClusters = totalClusters;
        Image = new byte[(long)totalClusters * Cluster];
        _bitmap = new byte[totalClusters / 8];
        WriteBoot();
        for (var c = 0L; c < MftLcn + MftClusters; c++) MarkUsed(c);
        AddRecord(0, inUse: true, dir: false, seq: 1, attrs: [FileName(5, 5, "$MFT"), NonResidentData([(MftLcn, MftClusters)], MftClusters * Cluster, MftClusters * Cluster)]);
        AddRecord(5, inUse: true, dir: true, seq: 5, attrs: [FileName(5, 5, ".")]);
    }

    public void MarkUsed(long cluster) => _bitmap[cluster >> 3] |= (byte)(1 << (int)(cluster & 7));

    public void WriteClusters(long lcn, byte[] data) => data.CopyTo(Image, lcn * Cluster);

    /// <summary>Writes $Bitmap (record 6). Call last, after all MarkUsed calls.</summary>
    public void Finish() => AddRecord(6, inUse: true, dir: false, seq: 6, attrs: [FileName(5, 5, "$Bitmap"), Resident(0x80, _bitmap)]);

    private void WriteBoot()
    {
        var s = Image.AsSpan(0, 512);
        s[0] = 0xEB; s[1] = 0x52; s[2] = 0x90;
        "NTFS    "u8.CopyTo(s[3..]);
        BinaryPrimitives.WriteUInt16LittleEndian(s[11..], Bps);
        s[13] = Spc;
        BinaryPrimitives.WriteUInt64LittleEndian(s[40..], (ulong)(TotalClusters * Spc - 1));
        BinaryPrimitives.WriteUInt64LittleEndian(s[48..], (ulong)MftLcn);
        BinaryPrimitives.WriteUInt64LittleEndian(s[56..], 2);
        s[64] = unchecked((byte)(sbyte)-10); // 2^10 = 1024-byte records
        s[68] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(s[72..], 0x1234_5678_9ABC_DEF0);
        s[510] = 0x55; s[511] = 0xAA;
    }

    public static byte[] Resident(uint type, byte[] value, string? name = null)
    {
        var nameBytes = name is null ? [] : Encoding.Unicode.GetBytes(name);
        var valueOffset = 24 + nameBytes.Length;
        valueOffset = (valueOffset + 7) & ~7;
        var length = (valueOffset + value.Length + 7) & ~7;
        var a = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(a, type);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)length);
        a[8] = 0;
        a[9] = (byte)(name?.Length ?? 0);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), 24);
        nameBytes.CopyTo(a, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(16), (uint)value.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(20), (ushort)valueOffset);
        value.CopyTo(a, valueOffset);
        return a;
    }

    public static byte[] FileName(long parent, ushort parentSeq, string name, long realSize = 0, byte ns = 1)
    {
        var v = new byte[66 + name.Length * 2];
        BinaryPrimitives.WriteUInt64LittleEndian(v, (ulong)parent | ((ulong)parentSeq << 48));
        var ft = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        for (var i = 8; i <= 32; i += 8) BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(i), ft);
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(48), realSize);
        v[64] = (byte)name.Length;
        v[65] = ns;
        Encoding.Unicode.GetBytes(name).CopyTo(v, 66);
        return Resident(0x30, v);
    }

    public static byte[] StandardInformation(DateTime created, DateTime modified)
    {
        var v = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(v, created.ToFileTimeUtc());
        BinaryPrimitives.WriteInt64LittleEndian(v.AsSpan(8), modified.ToFileTimeUtc());
        return Resident(0x10, v);
    }

    public static byte[] EncodeRuns(IEnumerable<(long Lcn, long Count)> runs)
    {
        var bytes = new List<byte>();
        long prev = 0;
        foreach (var (lcn, count) in runs)
        {
            var len = Minimal(count, signed: false);
            if (lcn < 0) { bytes.Add((byte)len.Length); bytes.AddRange(len); continue; }
            var off = Minimal(lcn - prev, signed: true);
            bytes.Add((byte)((off.Length << 4) | len.Length));
            bytes.AddRange(len);
            bytes.AddRange(off);
            prev = lcn;
        }
        bytes.Add(0);
        return bytes.ToArray();
    }

    private static byte[] Minimal(long v, bool signed)
    {
        for (var n = 1; n <= 8; n++)
        {
            var min = signed ? -(1L << (8 * n - 1)) : 0;
            var max = signed ? (1L << (8 * n - 1)) - 1 : (n == 8 ? long.MaxValue : (1L << (8 * n)) - 1);
            if (v >= min && v <= max) return BitConverter.GetBytes(v)[..n];
        }
        throw new ArgumentOutOfRangeException(nameof(v));
    }

    public static byte[] NonResidentData(IEnumerable<(long Lcn, long Count)> runs, long realSize, long initializedSize, long startVcn = 0)
    {
        var runBytes = EncodeRuns(runs);
        var length = (64 + runBytes.Length + 7) & ~7;
        var a = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(a, 0x80);
        BinaryPrimitives.WriteUInt32LittleEndian(a.AsSpan(4), (uint)length);
        a[8] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(10), 64);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(16), startVcn);
        BinaryPrimitives.WriteUInt16LittleEndian(a.AsSpan(32), 64);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(40), (realSize + Cluster - 1) / Cluster * Cluster);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(48), realSize);
        BinaryPrimitives.WriteInt64LittleEndian(a.AsSpan(56), initializedSize);
        runBytes.CopyTo(a, 64);
        return a;
    }

    public void AddRecord(long number, bool inUse, bool dir, ushort seq, byte[][] attrs, bool tear = false, long baseRecord = 0)
    {
        var r = new byte[RecordSize];
        "FILE"u8.CopyTo(r);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), 48);   // USA offset
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(6), 3);    // 1 + 2 sectors
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(16), seq);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(20), 56);  // first attribute
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(22), (ushort)((inUse ? 1 : 0) | (dir ? 2 : 0)));
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(28), RecordSize);
        BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(32), (ulong)baseRecord);
        var pos = 56;
        foreach (var a in attrs) { a.CopyTo(r, pos); pos += a.Length; }
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(pos), 0xFFFFFFFF);
        pos += 8;
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(24), (uint)pos);

        const ushort usn = 0x0042;
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(48), usn);
        for (var i = 1; i <= 2; i++)
        {
            var end = i * Bps - 2;
            r[48 + i * 2] = r[end];
            r[48 + i * 2 + 1] = r[end + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(end), tear && i == 2 ? (ushort)0x9999 : usn);
        }
        r.CopyTo(Image, MftLcn * Cluster + number * RecordSize);
    }
}
