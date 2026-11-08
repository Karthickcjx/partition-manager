using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PartitionManager.Core.Recovery;

namespace PartitionManager.Infrastructure.WindowsApi;

/// <summary>
/// Opens a volume (\\.\E: or \\?\Volume{guid}) for raw, read-only access (GENERIC_READ, sharing read and write so the
/// mounted file system keeps working). Requires administrator rights. Never writes.
/// </summary>
public sealed class RawVolumeReader : VolumeReader, IDisposable
{
    private readonly SafeFileHandle _handle;

    private RawVolumeReader(SafeFileHandle handle, long size, int sectorSize)
    {
        _handle = handle;
        SizeBytes = size;
        SectorSize = sectorSize;
    }

    public override long SizeBytes { get; }
    public override int SectorSize { get; }

    /// <param name="volume">A drive letter ("E") or a volume GUID path ("\\?\Volume{...}\").</param>
    public static RawVolumeReader Open(string volume, long sizeBytes, int sectorSize = 512)
    {
        var path = volume.Length == 1 ? $@"\\.\{char.ToUpperInvariant(volume[0])}:" : volume.TrimEnd('\\');
        if (!path.StartsWith(@"\\.\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Not a volume path.", nameof(volume));
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return new RawVolumeReader(handle, sizeBytes, sectorSize);
    }

    protected override void ReadAligned(long offset, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = RandomAccess.Read(_handle, buffer.AsSpan(total, count - total), offset + total);
            if (n <= 0) throw new IOException($"Volume read at {offset + total} returned no data.");
            total += n;
        }
    }

    public void Dispose() => _handle.Dispose();
}

public static class VolumeIdentity
{
    /// <summary>The \\?\Volume{guid}\ name of the volume that contains <paramref name="path"/>, or null.</summary>
    public static string? VolumeOf(string path)
    {
        var full = Path.GetFullPath(path);
        var mount = new StringBuilder(1024);
        if (!GetVolumePathName(full, mount, mount.Capacity)) return null;
        var name = new StringBuilder(1024);
        return GetVolumeNameForVolumeMountPoint(mount.ToString(), name, name.Capacity) ? name.ToString() : null;
    }

    /// <summary>True when <paramref name="destinationFolder"/> is on the volume being recovered (which must be refused).</summary>
    public static bool IsSameVolume(string sourceVolumeGuidPath, string destinationFolder)
    {
        var dest = VolumeOf(destinationFolder);
        return dest is null || string.Equals(dest.TrimEnd('\\'), sourceVolumeGuidPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathName(string fileName, StringBuilder volumePath, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);
}
