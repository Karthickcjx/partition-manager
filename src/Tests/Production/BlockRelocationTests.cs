using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using PartitionManager.PrivilegedHelper.Engine;
using Xunit;

namespace PartitionManager.Tests.Production;

/// <summary>In-memory sector disk with optional write corruption and call counting.</summary>
internal sealed class MemoryRawDisk(int sectorSize, long length) : IRawDisk
{
    public byte[] Bytes { get; } = new byte[length];
    public int SectorSize { get; } = sectorSize;
    public long Length => Bytes.Length;
    public int Flushes { get; private set; }
    public Func<long, bool>? CorruptWriteAt { get; set; }

    public void Read(long offset, byte[] buffer, int count) { Check(offset, count); Array.Copy(Bytes, offset, buffer, 0, count); }
    public void Write(long offset, byte[] buffer, int count)
    {
        Check(offset, count);
        Array.Copy(buffer, 0, Bytes, offset, count);
        if (CorruptWriteAt?.Invoke(offset) == true) Bytes[offset] ^= 0xFF;
    }
    public void Flush() => Flushes++;
    private void Check(long offset, int count)
    {
        if (offset % SectorSize != 0 || count % SectorSize != 0 || offset < 0 || offset + count > Length) throw new ArgumentOutOfRangeException(nameof(offset));
    }
    public void Dispose() { }
}

/// <summary>Returns a corrupted byte on the Nth read (simulates a marginal medium or controller).</summary>
internal sealed class FlakyReadDisk(int sectorSize, long length, int flipOnRead) : IRawDisk
{
    private int _reads;
    public byte[] Bytes { get; } = new byte[length];
    public int SectorSize { get; } = sectorSize;
    public long Length => Bytes.Length;
    public void Read(long offset, byte[] buffer, int count) { Array.Copy(Bytes, offset, buffer, 0, count); if (++_reads == flipOnRead) buffer[0] ^= 0x01; }
    public void Write(long offset, byte[] buffer, int count) => Array.Copy(buffer, 0, Bytes, offset, count);
    public void Flush() { }
    public void Dispose() { }
}

public class BlockRelocationTests
{
    private const int Ss = 512;

    private static MemoryRawDisk RandomDisk(long length, int seed)
    {
        var d = new MemoryRawDisk(Ss, length);
        new Random(seed).NextBytes(d.Bytes);
        return d;
    }

    [Fact]
    public void Plan_copies_descending_when_moving_right_and_caps_chunks_at_the_shift()
    {
        var right = RelocationPlan.Create(1 << 20, (1 << 20) + 3 * Ss, 10 * Ss, Ss);
        Assert.True(right.Descending);
        Assert.Equal(3 * Ss, right.ChunkBytes);
        Assert.Equal([9 * Ss, 6 * Ss, 3 * Ss, 0L], right.ChunkOrder());
        var left = RelocationPlan.Create(8L << 20, 1L << 20, 64L << 20, Ss);
        Assert.False(left.Descending);
        Assert.Equal(RelocationPlan.DefaultMaxChunk, left.ChunkBytes);
        Assert.True(left.Overlaps);
    }

    [Theory]
    [InlineData(0L, 0L, 1024L)]          // same place
    [InlineData(100L, 2048L, 1024L)]     // unaligned source
    [InlineData(0L, 2048L, 1000L)]       // partial sector length
    [InlineData(-512L, 2048L, 1024L)]
    public void Plan_rejects_invalid_extents(long src, long dst, long len) =>
        Assert.ThrowsAny<ArgumentException>(() => RelocationPlan.Create(src, dst, len, Ss));

    public static IEnumerable<object[]> Moves()
    {
        // (source, destination, length) in sectors: disjoint both ways, overlapping both ways, 1-sector shift, partial last chunk.
        yield return [10L, 200L, 64L];
        yield return [200L, 10L, 64L];
        yield return [10L, 30L, 64L];
        yield return [30L, 10L, 64L];
        yield return [10L, 11L, 50L];
        yield return [11L, 10L, 50L];
        yield return [4L, 4L + 7L, 101L];
        yield return [4L + 7L, 4L, 101L];
    }

    [Theory]
    [MemberData(nameof(Moves))]
    public void Copy_moves_exactly_the_extent_and_nothing_else(long srcS, long dstS, long lenS)
    {
        var disk = RandomDisk(512 * Ss, (int)(srcS * 31 + dstS));
        var original = (byte[])disk.Bytes.Clone();
        long src = srcS * Ss, dst = dstS * Ss, len = lenS * Ss;
        var plan = RelocationPlan.Create(src, dst, len, Ss, maxChunk: 8 * Ss);
        var checkpoints = new List<long>();
        var result = BlockRelocator.Copy(disk, plan, (at, _) => checkpoints.Add(at));

        Assert.Equal(original.AsSpan((int)src, (int)len).ToArray(), disk.Bytes.AsSpan((int)dst, (int)len).ToArray());
        for (long i = 0; i < disk.Length; i++)
        {
            var inDest = i >= dst && i < dst + len;
            var inSrc = i >= src && i < src + len;
            if (!inDest && !inSrc) Assert.True(original[i] == disk.Bytes[i], $"byte {i} outside both extents changed");
        }
        Assert.Equal(result.SourceSha256, result.DestinationSha256);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original.AsSpan((int)src, (int)len))), result.SourceSha256);
        Assert.Equal(plan.ChunkOrder(), checkpoints);
        Assert.Equal(len, result.BytesCopied);
        Assert.True(disk.Flushes >= plan.ChunkCount);
    }

    [Fact]
    public void Randomized_overlapping_moves_preserve_data()
    {
        var rng = new Random(20261107);
        for (var trial = 0; trial < 300; trial++)
        {
            var len = rng.Next(1, 80) * (long)Ss;
            var src = rng.Next(0, 100) * (long)Ss;
            long dst;
            do dst = rng.Next(0, 100) * (long)Ss; while (dst == src);
            var disk = RandomDisk(200 * Ss + len, trial);
            var expected = disk.Bytes.AsSpan((int)src, (int)len).ToArray();
            BlockRelocator.Copy(disk, RelocationPlan.Create(src, dst, len, Ss, maxChunk: rng.Next(1, 16) * Ss));
            Assert.True(expected.AsSpan().SequenceEqual(disk.Bytes.AsSpan((int)dst, (int)len)), $"trial {trial}: {src}->{dst} len {len}");
        }
    }

    /// <summary>
    /// The resume property: after an interruption at any chunk, every source chunk that has not been copied yet is
    /// still intact. Without it, an interrupted overlapping move could never be completed.
    /// </summary>
    [Theory]
    [InlineData(10L, 13L, 60L)]
    [InlineData(13L, 10L, 60L)]
    [InlineData(10L, 11L, 40L)]
    [InlineData(40L, 10L, 33L)]
    public void Interruption_never_overwrites_source_chunks_not_yet_copied(long srcS, long dstS, long lenS)
    {
        long src = srcS * Ss, dst = dstS * Ss, len = lenS * Ss;
        var plan = RelocationPlan.Create(src, dst, len, Ss, maxChunk: 4 * Ss);
        var order = plan.ChunkOrder().ToList();
        for (var stopAt = 0; stopAt < order.Count; stopAt++)
        {
            var disk = RandomDisk(256 * Ss, stopAt);
            var original = (byte[])disk.Bytes.Clone();
            var stop = order[stopAt];
            var ex = Assert.Throws<IOException>(() => BlockRelocator.Copy(disk, plan, null, (at, _) => { if (at == stop) throw new IOException("power cut"); }));
            Assert.Equal("power cut", ex.Message);
            foreach (var pending in order.Skip(stopAt))
            {
                var n = (int)Math.Min(plan.ChunkBytes, len - pending);
                Assert.True(original.AsSpan((int)(src + pending), n).SequenceEqual(disk.Bytes.AsSpan((int)(src + pending), n)),
                    $"stop at chunk {stopAt}: uncopied source chunk +{pending} was modified");
            }
        }
    }

    [Fact]
    public void Failed_non_overlapping_copy_leaves_the_source_untouched()
    {
        var disk = RandomDisk(400 * Ss, 7);
        var original = (byte[])disk.Bytes.Clone();
        var plan = RelocationPlan.Create(10 * Ss, 200 * Ss, 100 * Ss, Ss, maxChunk: 8 * Ss);
        Assert.Throws<IOException>(() => BlockRelocator.Copy(disk, plan, null, (_, copied) => { if (copied > 40 * Ss) throw new IOException("device removed"); }));
        Assert.True(original.AsSpan(10 * Ss, 100 * Ss).SequenceEqual(disk.Bytes.AsSpan(10 * Ss, 100 * Ss)));
    }

    [Fact]
    public void Corrupted_write_is_caught_by_read_back_before_continuing()
    {
        var disk = RandomDisk(400 * Ss, 9);
        disk.CorruptWriteAt = offset => offset == 200 * Ss + 16 * Ss;
        var plan = RelocationPlan.Create(10 * Ss, 200 * Ss, 64 * Ss, Ss, maxChunk: 8 * Ss);
        var ex = Assert.Throws<RelocationException>(() => BlockRelocator.Copy(disk, plan));
        Assert.Equal("CHUNK_VERIFY_FAILED", ex.Code);
    }

    /// <summary>A source read that differs from the verification pass is caught before it overwrites anything.</summary>
    [Fact]
    public void Unstable_source_read_is_refused_before_it_is_written()
    {
        var disk = new FlakyReadDisk(Ss, 400 * Ss, flipOnRead: 17); // 16 pre-pass reads, then the first copy read
        new Random(11).NextBytes(disk.Bytes);
        var before = (byte[])disk.Bytes.Clone();
        var progress = new RelocationProgress { Overlapping = true };
        var ex = Assert.Throws<RelocationException>(() => BlockRelocator.Copy(disk, RelocationPlan.Create(10 * Ss, 14 * Ss, 64 * Ss, Ss, maxChunk: 4 * Ss), null, progress));
        Assert.Equal("SOURCE_READ_MISMATCH", ex.Code);
        Assert.Equal(before, disk.Bytes);                        // the bad chunk was the first copy read: nothing written
        Assert.Equal(RelocationPhase.NothingWritten, progress.Phase);
    }

    [Fact]
    public void Progress_records_the_phase_reached()
    {
        var disk = RandomDisk(400 * Ss, 13);
        var progress = new RelocationProgress();
        BlockRelocator.Copy(disk, RelocationPlan.Create(10 * Ss, 200 * Ss, 64 * Ss, Ss, maxChunk: 8 * Ss), null, progress);
        Assert.Equal(RelocationPhase.CopiedNotSwitched, progress.Phase);
        Assert.Equal(64 * Ss, progress.BytesCopied);

        var failing = RandomDisk(400 * Ss, 14);
        var p2 = new RelocationProgress();
        Assert.Throws<IOException>(() => BlockRelocator.Copy(failing, RelocationPlan.Create(10 * Ss, 200 * Ss, 64 * Ss, Ss, maxChunk: 8 * Ss), null,
            (_, copied) => { if (copied > 0) throw new IOException("x"); }, p2));
        Assert.Equal(RelocationPhase.Copying, p2.Phase);
    }

    [Fact]
    public void Extent_past_the_end_of_the_disk_is_refused_before_any_write()
    {
        var disk = RandomDisk(100 * Ss, 3);
        var before = (byte[])disk.Bytes.Clone();
        var ex = Assert.Throws<RelocationException>(() => BlockRelocator.Copy(disk, RelocationPlan.Create(0, 60 * Ss, 50 * Ss, Ss)));
        Assert.Equal("OUT_OF_RANGE", ex.Code);
        Assert.Equal(before, disk.Bytes);
    }

    // ---------------------------------------------------------------- boot sectors

    private static void Oem(byte[] b, int at, string text) => Encoding.ASCII.GetBytes(text).CopyTo(b, at);

    [Fact]
    public void Ntfs_hidden_sectors_are_updated_in_primary_and_backup_boot_sectors()
    {
        const long start = 2048L * Ss, total = 1000;
        var disk = new MemoryRawDisk(Ss, 4096L * Ss);
        foreach (var at in new[] { start, start + total * Ss })
        {
            var s = new byte[Ss];
            Oem(s, 3, "NTFS    ");
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x0B), Ss);
            BinaryPrimitives.WriteUInt32LittleEndian(s.AsSpan(0x1C), 63);
            BinaryPrimitives.WriteInt64LittleEndian(s.AsSpan(0x28), total);
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(510), 0xAA55);
            s.CopyTo(disk.Bytes, at);
        }
        var note = BootSectorRelocation.Update(disk, start, (total + 1) * Ss);
        Assert.Contains("primary and backup", note);
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(disk.Bytes.AsSpan((int)start + 0x1C)));
        Assert.Equal(2048u, BinaryPrimitives.ReadUInt32LittleEndian(disk.Bytes.AsSpan((int)(start + total * Ss) + 0x1C)));
    }

    [Fact]
    public void Fat32_hidden_sectors_are_updated_in_primary_and_backup_boot_sectors()
    {
        const long start = 4096L * Ss;
        var disk = new MemoryRawDisk(Ss, 8192L * Ss);
        foreach (var at in new[] { start, start + 6 * Ss })
        {
            var s = new byte[Ss];
            Oem(s, 0x52, "FAT32   ");
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(0x32), 6);
            BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(510), 0xAA55);
            s.CopyTo(disk.Bytes, at);
        }
        Assert.Contains("FAT32", BootSectorRelocation.Update(disk, start, 1000 * Ss));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(disk.Bytes.AsSpan((int)start + 0x1C)));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32LittleEndian(disk.Bytes.AsSpan((int)start + 6 * Ss + 0x1C)));
    }

    [Fact]
    public void ExFat_partition_offset_is_updated_and_the_boot_checksum_recomputed()
    {
        const long start = 3000L * Ss;
        var disk = new MemoryRawDisk(Ss, 8192L * Ss);
        foreach (var region in new[] { 0L, 12L })
        {
            var boot = new byte[12 * Ss];
            Oem(boot, 3, "EXFAT   ");
            boot[0x6C] = 9; // 2^9 = 512 bytes per sector
            BinaryPrimitives.WriteUInt64LittleEndian(boot.AsSpan(0x40), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(boot.AsSpan(510), 0xAA55);
            boot[106] = 0x5A; boot[112] = 0x33; // excluded fields
            boot.CopyTo(disk.Bytes, start + region * Ss);
        }
        Assert.Contains("checksum recomputed", BootSectorRelocation.Update(disk, start, 4000 * Ss));
        foreach (var region in new[] { 0L, 12L })
        {
            var boot = disk.Bytes.AsSpan((int)(start + region * Ss), 12 * Ss).ToArray();
            Assert.Equal(3000ul, BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(0x40)));
            var sum = BootSectorRelocation.ExFatBootChecksum(boot, Ss);
            for (var i = 11 * Ss; i < 12 * Ss; i += 4) Assert.Equal(sum, BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(i)));
        }
    }

    [Fact]
    public void Unknown_boot_sector_is_left_unchanged()
    {
        var disk = RandomDisk(100 * Ss, 5);
        var before = (byte[])disk.Bytes.Clone();
        Assert.Contains("unchanged", BootSectorRelocation.Update(disk, 10 * Ss, 50 * Ss));
        Assert.Equal(before, disk.Bytes);
    }
}
