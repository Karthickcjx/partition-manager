using System.Diagnostics;

namespace PartitionManager.Core.Recovery;

public enum RecoveryChance
{
    /// <summary>Data lives inside the MFT record itself and is intact.</summary>
    Excellent,
    /// <summary>None of the file's clusters are currently allocated to anything else.</summary>
    Good,
    /// <summary>Some clusters have been reused; the recovered file will be partly wrong.</summary>
    Partial,
    /// <summary>All clusters have been reused; recovery would produce other data.</summary>
    Overwritten,
    /// <summary>The file is described across several MFT records (attribute list); not supported yet.</summary>
    Unsupported,
}

public sealed record RecoverableFile(
    long RecordNumber,
    string Name,
    string Folder,
    long SizeBytes,
    DateTime? Modified,
    DateTime? Created,
    RecoveryChance Chance,
    NtfsData? Data)
{
    public string FullPath => Folder.TrimEnd('\\') + "\\" + Name;
}

public sealed record NtfsScanResult(
    NtfsBootSector Boot,
    IReadOnlyList<RecoverableFile> Files,
    long RecordsScanned,
    long CorruptRecords,
    TimeSpan Elapsed);

public sealed record ScanProgress(double Fraction, long RecordsScanned, int FilesFound);

/// <summary>
/// Finds deleted files on an NTFS volume by reading the whole MFT (read-only). Deleted files keep their MFT record
/// (flagged not-in-use) until NTFS reuses it, and their clusters until something else is written there.
/// </summary>
public static class NtfsRecoveryScanner
{
    private const long RootDirectoryRecord = 5;
    private const long BitmapRecord = 6;
    private const int ChunkBytes = 4 * 1024 * 1024;

    private sealed record DirEntry(string Name, long Parent, ushort ParentSequence, ushort Sequence);

    public static NtfsBootSector ReadBoot(VolumeReader volume) =>
        NtfsBootSector.TryParse(volume.Read(0, 512)) ?? throw new NtfsFormatException("The volume does not contain an NTFS boot sector.");

    public static NtfsScanResult Scan(VolumeReader volume, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var boot = ReadBoot(volume);
        var cluster = boot.ClusterSize;
        var recordSize = boot.MftRecordSize;
        var maxClusters = boot.ClusterCount;

        var mft = MftRecordParser.Parse(volume.Read(boot.MftLcn * cluster, recordSize), boot.BytesPerSector, 0, maxClusters)
                  ?? throw new NtfsFormatException("MFT record 0 ($MFT) is unreadable.");
        if (mft.Data is not { IsResident: false } mftData) throw new NtfsFormatException("$MFT has no data runs.");
        var totalRecords = mftData.RealSize / recordSize;

        var directories = new Dictionary<long, DirEntry>();
        var candidates = new List<MftRecord>();
        long scanned = 0, corrupt = 0;
        byte[]? bitmap = null;

        var buffer = new byte[ChunkBytes];
        long recordNumber = 0;
        foreach (var run in mftData.Runs)
        {
            if (run.IsSparse) { recordNumber += run.ClusterCount * cluster / recordSize; continue; }
            var runBytes = run.ClusterCount * cluster;
            for (long done = 0; done < runBytes && recordNumber < totalRecords; done += ChunkBytes)
            {
                ct.ThrowIfCancellationRequested();
                var take = (int)Math.Min(ChunkBytes, runBytes - done);
                take -= take % recordSize;
                volume.Read(run.Lcn * cluster + done, buffer, 0, take);
                for (var off = 0; off + recordSize <= take && recordNumber < totalRecords; off += recordSize, recordNumber++)
                {
                    scanned++;
                    var span = buffer.AsSpan(off, recordSize);
                    if (!span[..4].SequenceEqual("FILE"u8)) continue; // never-used record
                    var rec = MftRecordParser.Parse(span, boot.BytesPerSector, recordNumber, maxClusters);
                    if (rec is null) { corrupt++; continue; }
                    if (rec.BaseRecord != 0) continue; // extension record
                    if (rec.IsDirectory && rec.BestName is { } dn)
                        directories[recordNumber] = new DirEntry(dn.Name, dn.ParentRecord, dn.ParentSequence, rec.Sequence);
                    if (recordNumber == BitmapRecord && rec.InUse && rec.Data is { } bm)
                        bitmap = ReadAll(volume, boot, bm, maxBytes: (int)Math.Min(int.MaxValue, (maxClusters + 7) / 8 + cluster));
                    if (!rec.InUse && !rec.IsDirectory && rec.BestName is not null && (rec.Data is not null || rec.HasAttributeList))
                        candidates.Add(rec);
                }
                progress?.Report(new ScanProgress(totalRecords == 0 ? 1 : (double)recordNumber / totalRecords, scanned, candidates.Count));
            }
        }

        var files = new List<RecoverableFile>(candidates.Count);
        foreach (var rec in candidates)
        {
            var name = rec.BestName!;
            var chance = rec.HasAttributeList && rec.Data is null ? RecoveryChance.Unsupported
                : rec.Data!.IsResident ? RecoveryChance.Excellent
                : rec.HasAttributeList ? RecoveryChance.Unsupported
                : Assess(rec.Data, bitmap);
            files.Add(new RecoverableFile(rec.Number, name.Name, BuildPath(name.ParentRecord, name.ParentSequence, directories),
                rec.Data?.RealSize ?? name.RealSize, rec.Modified ?? name.Modified, rec.Created, chance, rec.Data));
        }
        progress?.Report(new ScanProgress(1, scanned, files.Count));
        return new NtfsScanResult(boot, files, scanned, corrupt, sw.Elapsed);
    }

    private static RecoveryChance Assess(NtfsData data, byte[]? bitmap)
    {
        if (bitmap is null) return RecoveryChance.Good;
        long total = 0, reused = 0;
        foreach (var run in data.Runs.Where(r => !r.IsSparse))
        {
            for (var c = run.Lcn; c < run.Lcn + run.ClusterCount; c++)
            {
                total++;
                var byteIndex = c >> 3;
                if (byteIndex < bitmap.Length && (bitmap[byteIndex] & (1 << (int)(c & 7))) != 0) reused++;
            }
        }
        return reused == 0 ? RecoveryChance.Good : reused == total ? RecoveryChance.Overwritten : RecoveryChance.Partial;
    }

    private static string BuildPath(long parent, ushort parentSequence, Dictionary<long, DirEntry> dirs)
    {
        var parts = new List<string>();
        var seen = new HashSet<long>();
        var current = parent;
        var expectedSequence = parentSequence;
        while (current != RootDirectoryRecord)
        {
            if (!seen.Add(current) || parts.Count > 64 || !dirs.TryGetValue(current, out var dir))
                return "\\[unknown folder]" + (parts.Count == 0 ? "" : "\\" + string.Join("\\", Enumerable.Reverse(parts)));
            // A different sequence number means the folder record was reused for something else.
            if (expectedSequence != 0 && dir.Sequence != expectedSequence && dir.Sequence != expectedSequence + 1)
                return "\\[unknown folder]" + (parts.Count == 0 ? "" : "\\" + string.Join("\\", Enumerable.Reverse(parts)));
            parts.Add(dir.Name);
            current = dir.Parent;
            expectedSequence = dir.ParentSequence;
        }
        return "\\" + string.Join("\\", Enumerable.Reverse(parts));
    }

    private static byte[] ReadAll(VolumeReader v, NtfsBootSector boot, NtfsData data, int maxBytes)
    {
        if (data.IsResident) return data.ResidentBytes;
        var size = (int)Math.Min(data.RealSize, maxBytes);
        var result = new byte[size];
        long written = 0;
        foreach (var run in data.Runs)
        {
            var bytes = Math.Min(run.ClusterCount * boot.ClusterSize, size - written);
            if (bytes <= 0) break;
            if (!run.IsSparse) v.Read(run.Lcn * boot.ClusterSize, result, (int)written, (int)bytes);
            written += bytes;
        }
        return result;
    }
}

public sealed record RecoveredFile(RecoverableFile Source, string OutputPath, long BytesWritten);

/// <summary>Writes a deleted file's data to a new file. Never writes to the scanned volume (enforced by the caller).</summary>
public static class FileRecoverer
{
    private const int BufferBytes = 1024 * 1024;

    public static RecoveredFile Recover(VolumeReader volume, NtfsBootSector boot, RecoverableFile file, string destinationRoot, bool keepFolders = true, CancellationToken ct = default)
    {
        if (file.Data is null || file.Chance == RecoveryChance.Unsupported)
            throw new NtfsFormatException($"{file.Name}: this file's data layout is not supported yet.");
        var folder = destinationRoot;
        if (keepFolders)
            foreach (var part in file.Folder.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                folder = Path.Combine(folder, SafeName(part));
        Directory.CreateDirectory(folder);
        var target = UniquePath(Path.Combine(folder, SafeName(file.Name)));

        long written = 0;
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes))
        {
            var data = file.Data;
            if (data.IsResident)
            {
                output.Write(data.ResidentBytes, 0, (int)Math.Min(data.ResidentBytes.Length, data.RealSize));
                written = output.Length;
            }
            else
            {
                var buffer = new byte[BufferBytes];
                var cluster = boot.ClusterSize;
                foreach (var run in data.Runs)
                {
                    var runBytes = run.ClusterCount * cluster;
                    for (long done = 0; done < runBytes && written < data.RealSize; )
                    {
                        ct.ThrowIfCancellationRequested();
                        var take = (int)Math.Min(Math.Min(BufferBytes, runBytes - done), data.RealSize - written);
                        if (run.IsSparse || written >= data.InitializedSize)
                            Array.Clear(buffer, 0, take); // sparse or beyond valid data length reads as zeros
                        else
                            volume.Read(run.Lcn * cluster + done, buffer, 0, take);
                        if (!run.IsSparse && written < data.InitializedSize && written + take > data.InitializedSize)
                            Array.Clear(buffer, (int)(data.InitializedSize - written), (int)(written + take - data.InitializedSize));
                        output.Write(buffer, 0, take);
                        done += take;
                        written += take;
                    }
                    if (written >= data.RealSize) break;
                }
                if (written < data.RealSize) output.SetLength(written); // runs ended early: keep what exists
            }
        }
        if (file.Modified is { } m) try { File.SetLastWriteTimeUtc(target, m); } catch (IOException) { }
        return new RecoveredFile(file, target, written);
    }

    /// <summary>
    /// Reads up to <paramref name="count"/> bytes of a deleted file's data starting at <paramref name="position"/>, with
    /// the same rules as <see cref="Recover"/> (sparse runs and bytes beyond the initialized size read as zeros).
    /// Returns the number of bytes produced; fewer than requested only at the end of the data or where runs end early.
    /// </summary>
    public static int ReadAt(VolumeReader volume, NtfsBootSector boot, RecoverableFile file, long position, byte[] buffer, int count)
    {
        if (file.Data is null || file.Chance == RecoveryChance.Unsupported)
            throw new NtfsFormatException($"{file.Name}: this file's data layout is not supported yet.");
        var data = file.Data;
        if (position < 0 || count < 0 || count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(position));
        var want = (int)Math.Max(0, Math.Min(count, data.RealSize - position));
        if (want == 0) return 0;
        if (data.IsResident)
        {
            var available = (int)Math.Max(0, Math.Min(want, Math.Min(data.ResidentBytes.Length, data.RealSize) - position));
            if (available > 0) Array.Copy(data.ResidentBytes, position, buffer, 0, available);
            return available;
        }
        var cluster = (long)boot.ClusterSize;
        long cur = position, runStart = 0;
        var filled = 0;
        foreach (var run in data.Runs)
        {
            var runEnd = runStart + run.ClusterCount * cluster;
            while (filled < want && cur >= runStart && cur < runEnd)
            {
                var take = (int)Math.Min(want - filled, runEnd - cur);
                if (run.IsSparse || cur >= data.InitializedSize)
                    Array.Clear(buffer, filled, take);
                else
                {
                    volume.Read(run.Lcn * cluster + (cur - runStart), buffer, filled, take);
                    if (cur + take > data.InitializedSize)
                        Array.Clear(buffer, filled + (int)(data.InitializedSize - cur), (int)(cur + take - data.InitializedSize));
                }
                filled += take; cur += take;
            }
            runStart = runEnd;
            if (filled >= want) break;
        }
        return filled;
    }

    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>Names come from disk and are untrusted: strip separators, reserved names and dot-only names.</summary>
    public static string SafeName(string name)
    {
        var clean = new string(name.Select(c => Invalid.Contains(c) || c < 32 ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length == 0 || clean.All(c => c == '.')) clean = "_";
        var stem = clean.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            clean = "_" + clean;
        return clean.Length > 200 ? clean[..200] : clean;
    }

    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
