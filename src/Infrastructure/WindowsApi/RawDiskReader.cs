using Microsoft.Win32.SafeHandles;
using PartitionManager.Core.PartitionTables;

namespace PartitionManager.Infrastructure.WindowsApi;

/// <summary>
/// Opens \\.\PhysicalDriveN for reading only (GENERIC_READ, shared read/write) so the partition table can be
/// verified independently of the Windows storage stack. Requires administrator rights; never writes.
/// </summary>
public sealed class RawDiskReader : ISectorSource, IDisposable
{
    private readonly SafeFileHandle _handle;

    private RawDiskReader(SafeFileHandle handle, int sectorSize, long sizeBytes)
    {
        _handle = handle;
        SectorSize = sectorSize;
        SizeBytes = sizeBytes;
    }

    public int SectorSize { get; }
    public long SizeBytes { get; }

    public static RawDiskReader Open(int diskNumber, int sectorSize, long sizeBytes)
    {
        var handle = File.OpenHandle($@"\\.\PhysicalDrive{diskNumber}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new RawDiskReader(handle, sectorSize, sizeBytes);
    }

    public byte[] ReadSectors(ulong lba, int count)
    {
        // Device reads must start and end on sector boundaries.
        var buffer = new byte[count * SectorSize];
        var offset = checked((long)lba * SectorSize);
        var total = 0;
        while (total < buffer.Length)
        {
            var n = RandomAccess.Read(_handle, buffer.AsSpan(total), offset + total);
            if (n <= 0) throw new PartitionTableException($"Read of LBA {lba} returned no data.");
            total += n;
        }
        return buffer;
    }

    public void Dispose() => _handle.Dispose();
}
