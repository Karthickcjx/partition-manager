using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PartitionManager.PrivilegedHelper.Engine;

/// <summary>
/// Raw access to \\.\PhysicalDriveN for block-level moves. Windows allows writes to a volume's sectors through the
/// disk handle only while that volume is locked or dismounted, which <see cref="Win32VolumeLock"/> guarantees.
/// Write-through plus FlushFileBuffers after every chunk.
/// </summary>
internal sealed class Win32RawDisk : IRawDisk
{
    private readonly SafeFileHandle _handle;
    public int SectorSize { get; }
    public long Length { get; }
    public int DiskNumber { get; }
    internal SafeFileHandle Handle => _handle;

    public Win32RawDisk(int diskNumber)
    {
        DiskNumber = diskNumber;
        _handle = Native.CreateFileW($@"\\.\PhysicalDrive{diskNumber}", Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE, IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_WRITE_THROUGH, IntPtr.Zero);
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open disk {diskNumber} for raw access");
        var geometry = Native.Ioctl(_handle, Native.IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, null, 256);
        SectorSize = BinaryPrimitives.ReadInt32LittleEndian(geometry.AsSpan(20));
        Length = BinaryPrimitives.ReadInt64LittleEndian(Native.Ioctl(_handle, Native.IOCTL_DISK_GET_LENGTH_INFO, null, 8));
        if (SectorSize is < 512 or > 65536 || (SectorSize & (SectorSize - 1)) != 0) throw new IOException($"Unexpected sector size {SectorSize}.");
    }

    public void Read(long offset, byte[] buffer, int count)
    {
        Check(offset, count);
        for (var done = 0; done < count;)
        {
            var n = RandomAccess.Read(_handle, buffer.AsSpan(done, count - done), offset + done);
            if (n <= 0) throw new IOException($"Short read at {offset + done} on disk {DiskNumber}.");
            done += n;
        }
    }

    public void Write(long offset, byte[] buffer, int count)
    {
        Check(offset, count);
        RandomAccess.Write(_handle, buffer.AsSpan(0, count), offset);
    }

    public void Flush()
    {
        if (!Native.FlushFileBuffers(_handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "FlushFileBuffers failed on the disk");
    }

    private void Check(long offset, int count)
    {
        if (offset % SectorSize != 0 || count % SectorSize != 0 || offset < 0 || offset + count > Length)
            throw new ArgumentOutOfRangeException(nameof(offset), $"Unaligned or out-of-range I/O at {offset} (+{count}).");
    }

    /// <summary>GPT type written while an overlapping move is in progress: unknown to Windows, so it is never mounted.</summary>
    public static readonly Guid MoveInProgressGptType = new("b7e1c6a0-5f3e-4d7a-9c2b-6d0a1e5f7c31");
    /// <summary>MBR type written while an overlapping move is in progress (not a type Windows recognises or mounts).</summary>
    public const byte MoveInProgressMbrType = 0x7F;

    private byte[] GetLayout() => Native.Ioctl(_handle, Native.IOCTL_DISK_GET_DRIVE_LAYOUT_EX, null, Native.LayoutHeader + 256 * Native.PartitionEntry);

    /// <summary>GPT DiskId or MBR signature from a layout buffer.</summary>
    private static string DiskIdentityOf(byte[] layout) =>
        BinaryPrimitives.ReadInt32LittleEndian(layout) == 1 ? "GPT:" + new Guid(layout.AsSpan(8, 16)) : "MBR:" + BinaryPrimitives.ReadUInt32LittleEndian(layout.AsSpan(8)).ToString("X8");

    /// <summary>
    /// Checks a layout buffer: exactly one entry is the validated partition (number, start, length, GPT partition GUID),
    /// the destination overlaps no other entry (including MBR extended containers), lies in the GPT usable area or,
    /// for MBR, below the 32-bit sector limit. Returns the entry index.
    /// </summary>
    private int CheckLayout(byte[] layout, int partitionNumber, long expectedStart, long size, Guid? partitionGuid, long destination)
    {
        var style = BinaryPrimitives.ReadInt32LittleEndian(layout);
        if (style is not (0 or 1)) throw new IOException("Only MBR and GPT disks can be moved on. Nothing was changed.");
        var count = BinaryPrimitives.ReadInt32LittleEndian(layout.AsSpan(4));
        var found = -1;
        for (var i = 0; i < count; i++)
        {
            var e = layout.AsSpan(Native.LayoutHeader + i * Native.PartitionEntry, Native.PartitionEntry);
            var start = BinaryPrimitives.ReadInt64LittleEndian(e[8..]);
            var length = BinaryPrimitives.ReadInt64LittleEndian(e[16..]);
            var number = BinaryPrimitives.ReadInt32LittleEndian(e[24..]);
            if (length <= 0) continue;
            if (number == partitionNumber)
            {
                var guidOk = style != 1 || partitionGuid is null || new Guid(e.Slice(48, 16)) == partitionGuid;
                if (start != expectedStart || length != size || !guidOk || found >= 0)
                    throw new IOException($"Partition {partitionNumber} on disk {DiskNumber} does not match the validated target. Nothing was changed.");
                found = i;
                continue;
            }
            if (start < destination + size && destination < start + length)
                throw new IOException($"The destination overlaps partition {number} on disk {DiskNumber}. Nothing was changed.");
        }
        if (found < 0) throw new IOException($"Partition {partitionNumber} was not found on disk {DiskNumber}. Nothing was changed.");
        if (style == 1)
        {
            var usableStart = BinaryPrimitives.ReadInt64LittleEndian(layout.AsSpan(24));
            var usableLength = BinaryPrimitives.ReadInt64LittleEndian(layout.AsSpan(32));
            if (destination < usableStart || destination + size > usableStart + usableLength)
                throw new IOException("The destination is outside the GPT usable area. Nothing was changed.");
        }
        else
        {
            if (destination < SectorSize || destination + size > Length) throw new IOException("The destination is outside the disk. Nothing was changed.");
            if ((destination + size) / SectorSize > uint.MaxValue) throw new IOException("The destination is beyond the MBR 32-bit sector limit (2 TiB at 512-byte sectors). Nothing was changed.");
        }
        return found;
    }

    /// <summary>
    /// Fails (before any write) unless this handle is a file-backed virtual disk whose layout contains exactly the
    /// validated partition and a free destination. Returns the disk's partition-table identity (GPT DiskId / MBR
    /// signature); pass it back as <paramref name="expectedIdentity"/> on later checks to detect a swapped disk.
    /// </summary>
    public string VerifyTarget(int partitionNumber, long offset, long size, Guid? partitionGuid, long destination, string? expectedIdentity = null, bool expectVirtual = true)
    {
        var query = new byte[12]; // STORAGE_PROPERTY_QUERY { StorageDeviceProperty, PropertyStandardQuery }
        var descriptor = Native.Ioctl(_handle, Native.IOCTL_STORAGE_QUERY_PROPERTY, query, 1024);
        var busType = BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(28));
        // The engine decided virtual vs enrolled physical test disk; the opened handle must agree.
        if ((busType == Native.BusTypeFileBackedVirtual) != expectVirtual)
            throw new IOException($"Disk {DiskNumber} is not the expected kind of disk (bus type {busType}). Nothing was changed.");
        var layout = GetLayout();
        var identity = DiskIdentityOf(layout);
        if (expectedIdentity is not null && identity != expectedIdentity)
            throw new IOException($"Disk {DiskNumber} is no longer the validated disk ({identity} instead of {expectedIdentity}). Nothing was changed.");
        CheckLayout(layout, partitionNumber, offset, size, partitionGuid, destination);
        return identity;
    }

    /// <summary>
    /// Rewrites one partition entry through IOCTL_DISK_SET_DRIVE_LAYOUT_EX (Windows writes the primary and backup GPT
    /// and their CRCs, or the MBR entry): new start and/or a replacement type. The layout read immediately before is
    /// re-verified against the validated target, disk identity and destination; every other entry is passed back
    /// exactly as read. Returns the entry's previous type (GPT type GUID bytes or the MBR type byte).
    /// </summary>
    public byte[] SetPartitionEntry(int partitionNumber, long currentStart, long size, Guid? partitionGuid, long newStart, long destination,
        string expectedIdentity, byte[]? newType, RelocationProgress? progress = null)
    {
        var layout = GetLayout();
        var identity = DiskIdentityOf(layout);
        if (identity != expectedIdentity) throw new IOException($"Disk {DiskNumber} is no longer the validated disk; the table was not changed.");
        var index = CheckLayout(layout, partitionNumber, currentStart, size, partitionGuid, destination);
        var style = BinaryPrimitives.ReadInt32LittleEndian(layout);
        var count = BinaryPrimitives.ReadInt32LittleEndian(layout.AsSpan(4));
        var entry = layout.AsSpan(Native.LayoutHeader + index * Native.PartitionEntry, Native.PartitionEntry);
        var oldType = style == 1 ? entry.Slice(32, 16).ToArray() : [entry[32]];
        BinaryPrimitives.WriteInt64LittleEndian(entry[8..], newStart);
        entry[28] = 1; // RewritePartition
        if (style == 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(entry[36..], checked((uint)(newStart / SectorSize))); // MBR HiddenSectors
            if (newType is not null) entry[32] = newType[0];
        }
        else if (newType is not null) newType.AsSpan(0, 16).CopyTo(entry.Slice(32, 16));
        if (progress is not null && newStart != currentStart) progress.TableUpdateIssued = true;
        Native.Ioctl(_handle, Native.IOCTL_DISK_SET_DRIVE_LAYOUT_EX, layout.AsSpan(0, Native.LayoutHeader + count * Native.PartitionEntry).ToArray(), 0);
        if (progress is not null && newStart != currentStart) progress.TableUpdated = true;
        try { Native.Ioctl(_handle, Native.IOCTL_DISK_UPDATE_PROPERTIES, null, 0); }
        catch (Win32Exception) { /* the table is written; partmgr re-reads it on the next layout query */ }
        return oldType;
    }

    /// <summary>The "move in progress" type for this disk's partition style.</summary>
    public byte[] MoveInProgressType()
    {
        var style = BinaryPrimitives.ReadInt32LittleEndian(GetLayout());
        return style == 1 ? MoveInProgressGptType.ToByteArray() : [MoveInProgressMbrType];
    }

    public void Dispose() => _handle.Dispose();
}

/// <summary>
/// Exclusive lock + dismount of one volume for the duration of a block move. Lock acquisition fails if any other
/// handle is open on the volume, which is the proof that nothing can change the file system during the copy.
/// </summary>
internal sealed class Win32VolumeLock : IDisposable
{
    private readonly SafeFileHandle _handle;

    private Win32VolumeLock(SafeFileHandle handle) => _handle = handle;

    /// <param name="volumeGuidPath">\\?\Volume{GUID}\ as reported by MSFT_Partition.AccessPaths.</param>
    public static Win32VolumeLock Acquire(string volumeGuidPath, TimeSpan wait)
    {
        if (!volumeGuidPath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Not a volume GUID path.", nameof(volumeGuidPath));
        var device = @"\\.\" + volumeGuidPath[4..].TrimEnd('\\');
        var handle = Native.CreateFileW(device, Native.GENERIC_READ | Native.GENERIC_WRITE, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot open the volume");
        var deadline = DateTime.UtcNow + wait;
        while (!Native.DeviceIoControl(handle, Native.FSCTL_LOCK_VOLUME, null, 0, null, 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            if (DateTime.UtcNow >= deadline)
            {
                handle.Dispose();
                throw new IOException($"The volume is in use (lock failed, error {error}). Close every program, window and command prompt using it, then retry. Nothing was changed.");
            }
            Thread.Sleep(250);
        }
        if (!Native.DeviceIoControl(handle, Native.FSCTL_DISMOUNT_VOLUME, null, 0, null, 0, out _, IntPtr.Zero))
        {
            var error = Marshal.GetLastWin32Error();
            Native.DeviceIoControl(handle, Native.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out _, IntPtr.Zero);
            handle.Dispose();
            throw new Win32Exception(error, "Dismount failed; nothing was changed");
        }
        return new Win32VolumeLock(handle);
    }

    public void Dispose()
    {
        Native.DeviceIoControl(_handle, Native.FSCTL_UNLOCK_VOLUME, null, 0, null, 0, out _, IntPtr.Zero);
        _handle.Dispose();
    }
}

internal static class Native
{
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3, FILE_FLAG_WRITE_THROUGH = 0x80000000;
    public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0, IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C,
        IOCTL_DISK_GET_DRIVE_LAYOUT_EX = 0x00070050, IOCTL_DISK_SET_DRIVE_LAYOUT_EX = 0x0007C054, IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140,
        FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_UNLOCK_VOLUME = 0x0009001C, FSCTL_DISMOUNT_VOLUME = 0x00090020,
        IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const int BusTypeFileBackedVirtual = 15;
    /// <summary>sizeof(DRIVE_LAYOUT_INFORMATION_EX) up to PartitionEntry, and sizeof(PARTITION_INFORMATION_EX).</summary>
    public const int LayoutHeader = 48, PartitionEntry = 144;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? inBuffer, int inSize, byte[]? outBuffer, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool FlushFileBuffers(SafeFileHandle handle);

    public static byte[] Ioctl(SafeFileHandle handle, uint code, byte[]? input, int outputSize)
    {
        var output = outputSize > 0 ? new byte[outputSize] : null;
        if (!DeviceIoControl(handle, code, input, input?.Length ?? 0, output, outputSize, out var returned, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"DeviceIoControl 0x{code:X8} failed");
        return output ?? [];
    }
}
