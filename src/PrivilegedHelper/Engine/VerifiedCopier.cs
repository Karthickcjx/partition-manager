using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace PartitionManager.PrivilegedHelper.Engine;

public sealed record CopyEntry(string RelativePath, bool IsDirectory, long Size, FileAttributes Attributes, DateTime CreatedUtc, DateTime ModifiedUtc);

public sealed record CopyManifest(IReadOnlyList<CopyEntry> Entries, long TotalBytes, IReadOnlyList<string> Problems)
{
    public int FileCount => Entries.Count(e => !e.IsDirectory);
    public bool IsSupported => Problems.Count == 0;
}

public sealed record CopyResult(IReadOnlyDictionary<string, string> Sha256, long BytesCopied);

public sealed class CopyException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Test-only fault injection; set in-process by tests, never reachable over IPC.</summary>
internal sealed class CopyFaults
{
    public Action<string>? BeforeFile { get; init; }
    public Action<string>? AfterCopy { get; init; }
}

/// <summary>
/// File-level copy with verification, used by the staged Move and Merge. It refuses content that a plain file copy
/// cannot reproduce faithfully (reparse points, EFS files, alternate data streams, hard links) instead of silently
/// losing it. Memory use is bounded by one 1 MiB buffer.
/// </summary>
public static class VerifiedCopier
{
    private const int BufferBytes = 1024 * 1024;
    private static readonly string[] SkippedRootFolders = ["System Volume Information", "$RECYCLE.BIN"];

    public static CopyManifest Scan(string root)
    {
        var entries = new List<CopyEntry>();
        var problems = new List<string>();
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> children;
            try { children = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false }).ToList(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { problems.Add($"Cannot list {Rel(root, dir)}: {ex.Message}"); continue; }

            foreach (var item in children)
            {
                var rel = Rel(root, item.FullName);
                if (string.Equals(dir.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && SkippedRootFolders.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
                    continue;
                var attrs = item.Attributes;
                if ((attrs & FileAttributes.ReparsePoint) != 0) { problems.Add($"{rel} is a reparse point (junction, symlink or cloud file)"); continue; }
                if ((attrs & FileAttributes.Encrypted) != 0) { problems.Add($"{rel} is EFS-encrypted"); continue; }
                if (item is DirectoryInfo d)
                {
                    entries.Add(new CopyEntry(rel, true, 0, attrs, d.CreationTimeUtc, d.LastWriteTimeUtc));
                    pending.Push(d.FullName);
                    continue;
                }
                var f = (FileInfo)item;
                if (HasAlternateStreams(f.FullName)) problems.Add($"{rel} has alternate data streams");
                if (LinkCount(f.FullName) > 1) problems.Add($"{rel} has hard links");
                entries.Add(new CopyEntry(rel, false, f.Length, attrs, f.CreationTimeUtc, f.LastWriteTimeUtc));
                total += f.Length;
            }
        }
        return new CopyManifest(entries.OrderBy(e => e.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(), total, problems);
    }

    internal static CopyResult Copy(CopyManifest manifest, string sourceRoot, string destinationRoot, CancellationToken ct, CopyFaults? faults = null)
    {
        if (!manifest.IsSupported) throw new CopyException("The source contains content that cannot be copied faithfully: " + string.Join("; ", manifest.Problems));
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long copied = 0;
        var buffer = new byte[BufferBytes];
        foreach (var e in manifest.Entries.Where(e => e.IsDirectory)) Directory.CreateDirectory(Path.Combine(destinationRoot, e.RelativePath));
        foreach (var e in manifest.Entries.Where(e => !e.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var src = Path.Combine(sourceRoot, e.RelativePath);
            var dst = Path.Combine(destinationRoot, e.RelativePath);
            try
            {
                faults?.BeforeFile?.Invoke(e.RelativePath);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.SequentialScan))
                using (var output = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.WriteThrough))
                {
                    int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        sha.AppendData(buffer, 0, n);
                        output.Write(buffer, 0, n);
                        copied += n;
                    }
                    output.Flush(true);
                }
                hashes[e.RelativePath] = Convert.ToHexString(sha.GetHashAndReset());
                File.SetCreationTimeUtc(dst, e.CreatedUtc);
                File.SetLastWriteTimeUtc(dst, e.ModifiedUtc);
                File.SetAttributes(dst, e.Attributes); // last, so read-only files still got their timestamps
                faults?.AfterCopy?.Invoke(dst);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CopyException($"Copying {e.RelativePath} failed: {ex.Message}", ex);
            }
        }
        foreach (var e in manifest.Entries.Where(e => e.IsDirectory).OrderByDescending(e => e.RelativePath.Length))
        {
            var dst = Path.Combine(destinationRoot, e.RelativePath);
            Directory.SetLastWriteTimeUtc(dst, e.ModifiedUtc);
            Directory.SetCreationTimeUtc(dst, e.CreatedUtc);
            new DirectoryInfo(dst).Attributes = e.Attributes;
        }
        return new CopyResult(hashes, copied);
    }

    /// <summary>Re-reads every destination file and compares content hash, size, attributes and modified time.</summary>
    public static IReadOnlyList<string> Verify(CopyManifest manifest, CopyResult copy, string destinationRoot)
    {
        var errors = new List<string>();
        var buffer = new byte[BufferBytes];
        foreach (var e in manifest.Entries)
        {
            var path = Path.Combine(destinationRoot, e.RelativePath);
            if (e.IsDirectory)
            {
                if (!Directory.Exists(path)) errors.Add($"missing folder {e.RelativePath}");
                continue;
            }
            if (!File.Exists(path)) { errors.Add($"missing {e.RelativePath}"); continue; }
            var info = new FileInfo(path);
            if (info.Length != e.Size) { errors.Add($"{e.RelativePath}: size {info.Length} ≠ {e.Size}"); continue; }
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.SequentialScan))
            {
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0) sha.AppendData(buffer, 0, n);
            }
            if (Convert.ToHexString(sha.GetHashAndReset()) != copy.Sha256[e.RelativePath]) errors.Add($"{e.RelativePath}: content differs (SHA-256)");
            if ((info.Attributes & ~FileAttributes.Archive) != (e.Attributes & ~FileAttributes.Archive)) errors.Add($"{e.RelativePath}: attributes differ");
            if (Math.Abs((info.LastWriteTimeUtc - e.ModifiedUtc).TotalSeconds) > 2) errors.Add($"{e.RelativePath}: modified time differs"); // FAT has 2 s resolution
        }
        return errors;
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path);

    // --- alternate data streams and hard links (documented Win32 APIs)

    private static bool HasAlternateStreams(string path)
    {
        var handle = FindFirstStreamW(path, 0, out var data, 0);
        if (handle.IsInvalid) return false;
        using (handle)
        {
            do
            {
                if (!data.cStreamName.Equals("::$DATA", StringComparison.Ordinal)) return true;
            } while (FindNextStreamW(handle, out data));
        }
        return false;
    }

    private static uint LinkCount(string path)
    {
        using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return GetFileInformationByHandle(h, out var info) ? info.NumberOfLinks : 1;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string cStreamName;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)] // FILETIME members are 4-byte aligned in the native struct
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    private sealed class FindHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern FindHandle FindFirstStreamW(string fileName, int infoLevel, out Win32FindStreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextStreamW(FindHandle handle, out Win32FindStreamData data);
    [DllImport("kernel32.dll")]
    private static extern bool FindClose(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);
}
