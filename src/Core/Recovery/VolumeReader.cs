using PartitionManager.Core.PartitionTables;

namespace PartitionManager.Core.Recovery;

/// <summary>
/// Read-only byte access to a volume (a partition's contents). Devices only accept sector-aligned reads, so callers
/// may read any range and this class widens it to whole sectors.
/// </summary>
public abstract class VolumeReader
{
    public abstract long SizeBytes { get; }
    public abstract int SectorSize { get; }

    /// <summary>Reads <paramref name="count"/> bytes at <paramref name="offset"/>; both are multiples of <see cref="SectorSize"/>.</summary>
    protected abstract void ReadAligned(long offset, byte[] buffer, int count);

    public void Read(long offset, byte[] destination, int destinationOffset, int count)
    {
        if (count == 0) return;
        if (offset < 0 || count < 0 || offset > SizeBytes - count)
            throw new IOException($"Read of {count} bytes at {offset} is outside the {SizeBytes}-byte volume.");
        var ss = SectorSize;
        var start = offset - offset % ss;
        var end = offset + count;
        var alignedEnd = end % ss == 0 ? end : end + ss - end % ss;
        if (start == offset && alignedEnd == end && destinationOffset == 0 && destination.Length >= count)
        {
            ReadAligned(offset, destination, count);
            return;
        }
        var temp = new byte[checked((int)(alignedEnd - start))];
        ReadAligned(start, temp, temp.Length);
        Array.Copy(temp, offset - start, destination, destinationOffset, count);
    }

    public byte[] Read(long offset, int count)
    {
        var b = new byte[count];
        Read(offset, b, 0, count);
        return b;
    }
}

/// <summary>A volume inside a disk image or disk, starting at <paramref name="partitionOffset"/>.</summary>
public sealed class PartitionVolumeReader(ISectorSource disk, long partitionOffset, long sizeBytes) : VolumeReader
{
    public override long SizeBytes => sizeBytes;
    public override int SectorSize => disk.SectorSize;

    protected override void ReadAligned(long offset, byte[] buffer, int count)
    {
        var ss = disk.SectorSize;
        if ((partitionOffset + offset) % ss != 0 || count % ss != 0) throw new IOException("Unaligned device read.");
        var data = disk.ReadSectors((ulong)((partitionOffset + offset) / ss), count / ss);
        Buffer.BlockCopy(data, 0, buffer, 0, count);
    }
}

/// <summary>A volume backed by any seekable stream (volume image files, tests).</summary>
public sealed class StreamVolumeReader(Stream stream, int sectorSize = 512) : VolumeReader
{
    public override long SizeBytes => stream.Length;
    public override int SectorSize => sectorSize;

    protected override void ReadAligned(long offset, byte[] buffer, int count)
    {
        lock (stream)
        {
            stream.Position = offset;
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n == 0) throw new IOException("Unexpected end of volume image.");
                read += n;
            }
        }
    }
}
