using PartitionManager.Core.Execution;
using PartitionManager.Core.FileSystems;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Models;
using PartitionManager.Core.Recovery;

namespace PartitionManager.PrivilegedHelper.Engine;

/// <summary>
/// Initialize, Move and Merge. Windows has no API that moves a partition's data or merges two volumes, so Move and
/// Merge are staged, file-level workflows with an explicit cutover point:
///   before cutover — every change is ours and is undone on failure (temporary partition/folder deleted, source made
///                    writable again); the source is never modified (it is read-only for the whole copy);
///   cutover        — the source partition is deleted only after every file was copied and re-verified by SHA-256;
///   after cutover  — failures are reported with the verified location of the data and manual recovery steps.
/// Neither operation is atomic, and file-level copying does not preserve volume serial numbers, ACLs (beyond
/// defaults), or object IDs; content that cannot be copied faithfully is refused up front.
/// </summary>
public sealed partial class WriteEngine
{
    private VolumeAccess Volumes => volumeAccess ?? DefaultVolumes;
    private static readonly VolumeAccess DefaultVolumes = new();

    /// <summary>Test-only fault injection for the copy phase (in-process only).</summary>
    internal CopyFaults? CopyFaults { get; init; }

    private const long MergeHeadroomBytes = 64 * ByteSize.MiB;

    private ValidationResult CheckStagedPreconditions(WriteOperationRequest r, DiskInfo disk, PartitionInfo? p, PartitionInfo? second)
    {
        switch (r.Kind)
        {
            case WriteOperationKind.InitializeDisk:
                if (disk.Style != PartitionStyle.Raw || disk.Partitions.Count > 0)
                    return ValidationResult.Fail("DISK_NOT_EMPTY", $"{disk.Title} is already initialized ({disk.Style}, {disk.Partitions.Count} partition(s)). Only blank disks can be initialized.");
                return ValidationResult.Ok;

            case WriteOperationKind.MovePartition:
            {
                var problem = MovableVolume(p!);
                if (problem is not null) return problem;
                if (p!.IsLogical) return ValidationResult.Fail("LOGICAL_PARTITION", "Logical partitions inside an MBR extended partition cannot be moved yet.");
                var dest = r.OffsetBytes!.Value;
                if (dest % DiskLayout.Alignment != 0) return ValidationResult.Fail("UNALIGNED", $"The new start must be a multiple of {ByteSize.Format(DiskLayout.Alignment)}.");
                var (min, max) = DiskLayout.MovableRange(disk, p);
                var inOwnRange = dest >= min && dest + p.SizeBytes <= max;
                var inOtherFree = DiskLayout.FreeSegments(disk).Any(s => s.OffsetBytes <= dest && dest + p.SizeBytes <= s.EndBytes);
                // Layout segments skip MBR extended containers; their whole extent (EBRs, logical partitions) is occupied.
                if (disk.Partitions.Any(x => x.IsExtendedContainer && dest < x.EndBytes && x.OffsetBytes < dest + p.SizeBytes))
                    return ValidationResult.Fail("NOT_ENOUGH_SPACE", "The destination overlaps the MBR extended partition.");
                if (!inOwnRange && !inOtherFree)
                    return ValidationResult.Fail("NOT_ENOUGH_SPACE", $"The partition ({ByteSize.Format(p.SizeBytes)}) does not fit at {ByteSize.Format(dest)}: the destination must lie in unallocated space, optionally overlapping the partition's own current position.");
                return ValidationResult.Ok;
            }

            case WriteOperationKind.MergePartitions:
            {
                var target = p!;
                var source = second!;
                if (MovableVolume(target) is { } t) return ValidationResult.Fail(t.Code, "Target: " + t.Message);
                if (MovableVolume(source) is { } s) return ValidationResult.Fail(s.Code, "Source: " + s.Message);
                if (target.FreeBytes is { } free && source.UsedBytes is { } used && free < used + MergeHeadroomBytes)
                    return ValidationResult.Fail("NOT_ENOUGH_SPACE", $"The target has {ByteSize.Format(free)} free; merging needs {ByteSize.Format(used + MergeHeadroomBytes)} (the source's data plus headroom).");
                return ValidationResult.Ok;
            }
        }
        return ValidationResult.Ok;
    }

    private static ValidationResult? MovableVolume(PartitionInfo p)
    {
        if (p.IsProtectedRole || p.IsExtendedContainer) return ValidationResult.Fail("PROTECTED_PARTITION", $"{PartitionTypes.RoleDisplayName(p.Role)} partitions cannot be moved or merged.");
        if (p.IsBoot || p.IsSystem) return ValidationResult.Fail("SYSTEM_PARTITION", "The partition hosts the running system.");
        if (!p.HasFileSystem) return ValidationResult.Fail("UNSUPPORTED_FILE_SYSTEM", "The partition has no file system Windows can mount.");
        if (!FileSystemRules.IsFormattable(p.FileSystem)) return ValidationResult.Fail("UNSUPPORTED_FILE_SYSTEM", $"{p.FileSystem} volumes cannot be moved or merged (only NTFS, FAT32 and exFAT).");
        if (p.IsEncrypted || p.BitLocker == BitLockerState.Locked) return ValidationResult.Fail("ENCRYPTED", "BitLocker volumes cannot be moved or merged.");
        return null;
    }

    private char FreeLetter(params char?[] exclude)
    {
        var used = Discover().UsedLetters;
        for (var c = 'Z'; c >= 'G'; c--)
            if (!used.Contains(c) && !exclude.Contains(c) && !DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == c)) return c;
        throw new StepException("NO_FREE_LETTER", "No free drive letter is available for the temporary mount.");
    }

    private sealed class Rollback
    {
        private readonly Stack<(string Name, Action Undo)> _undo = new();
        public void Push(string name, Action undo) => _undo.Push((name, undo));
        public void Commit() => _undo.Clear();
        public bool Pending => _undo.Count > 0;

        public string Run()
        {
            var notes = new List<string>();
            while (_undo.Count > 0)
            {
                var (name, undo) = _undo.Pop();
                try { undo(); notes.Add($"{name}: undone"); }
                catch (Exception ex) { notes.Add($"{name}: COULD NOT BE UNDONE ({ex.Message})"); }
            }
            return string.Join("; ", notes);
        }
    }

    private (StepFailure?, string) Fail(string code, string message, Rollback rollback) =>
        (new StepFailure(code, message + (rollback.Pending ? " Rolling back: " + rollback.Run() + "." : "")), "");

    private void MakeReadOnly(int n, PartitionInfo p, string root, List<string> steps, Rollback rollback)
    {
        Step(steps, "Make source read-only", () => backend.SetPartitionReadOnly(n, p.PartitionNumber, true));
        rollback.Push("source read-only attribute", () => backend.SetPartitionReadOnly(n, p.PartitionNumber, false));
        if (Volumes.CanWrite(root))
            throw new StepException("SOURCE_NOT_READONLY", "The source volume still accepts writes after being set read-only, so a consistent copy cannot be guaranteed.");
        steps.Add("Source verified read-only");
    }

    /// <summary>Deletes the source; a read-only partition may refuse deletion, in which case the attribute is cleared first.</summary>
    private void DeleteSource(int n, PartitionInfo p, List<string> steps)
    {
        try { Step(steps, "Delete original partition (cutover)", () => backend.DeletePartition(n, p.PartitionNumber)); }
        catch (StepException)
        {
            Step(steps, "Clear read-only attribute for deletion", () => backend.SetPartitionReadOnly(n, p.PartitionNumber, false));
            Step(steps, "Delete original partition (cutover, retry)", () => backend.DeletePartition(n, p.PartitionNumber));
        }
    }

    /// <summary>
    /// Block-level Move: every sector of the partition is copied, so file data, ACLs, alternate streams, object IDs,
    /// the volume serial number and (GPT) the partition GUID are preserved by construction. Guarantees, in order:
    ///   * the volume is locked and dismounted for the whole copy (fails if anything has it open: nothing changed);
    ///   * each chunk is read back after writing, and the full destination SHA-256 must equal the source SHA-256
    ///     taken before the first write; only then is the partition entry rewritten (by Windows);
    ///   * non-overlapping move: the original is untouched until the table update;
    ///   * overlapping move: the original is progressively overwritten; an interruption leaves a journal with one
    ///     durable checkpoint per chunk. Automatic resume is NOT implemented yet: restore the image/backup.
    /// </summary>
    private (StepFailure?, string) Move(WriteOperationRequest r, DiskInfo before, PartitionInfo src, List<string> steps)
    {
        var n = before.Number;
        var dest = r.OffsetBytes!.Value;
        var overlapping = Math.Abs(dest - src.OffsetBytes) < src.SizeBytes;
        RelocationResult result;
        var stepName = overlapping ? "Lock volume; overlapping block copy with per-chunk verification" : "Lock volume; block copy with per-chunk verification";
        try
        {
            result = backend.RelocatePartition(n, PartitionTarget.From(src), dest, null);
            steps.Add(stepName);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // The data state is decided from the relocation's recorded progress, never from message text.
            var progress = (ex as RelocationFailedException)?.Progress ?? new RelocationProgress { Overlapping = overlapping };
            if (ex is PostMutationException) progress = new RelocationProgress { Overlapping = overlapping, FirstWriteIssued = true, CopyVerified = true, TableUpdateIssued = true };
            var reason = (ex as RelocationFailedException)?.InnerException?.Message ?? ex.Message;
            return progress.Phase switch
            {
                RelocationPhase.NothingWritten => (new StepFailure(MoveNotStarted,
                    reason.Contains("Nothing was changed", StringComparison.OrdinalIgnoreCase) ? reason : reason.TrimEnd('.') + ". Nothing was changed."), ""),
                RelocationPhase.Copying when !overlapping => (new StepFailure("MOVE_FAILED", reason + " The copy stopped part-way. The original partition was not overwritten (the destination does not overlap it) and the partition table still points at it; the partial copy is in unallocated space."), ""),
                RelocationPhase.Copying => (new StepFailure("MOVE_INTERRUPTED", reason + $" The overlapping copy stopped after {ByteSize.Format(progress.BytesCopied)} of {ByteSize.Format(src.SizeBytes)}: the original location is partly overwritten. " +
                    "The partition was marked 'move in progress' before copying, so Windows will not mount it or write to it. Do not change this disk; keep the journal. Automatic resume is not available yet: restore from your backup or disk image."), ""),
                RelocationPhase.CopiedNotSwitched => (new StepFailure("MOVE_NOT_SWITCHED", reason + $" The complete copy was verified at {ByteSize.Format(dest)} (SHA-256 {progress.SourceSha256?[..16]}...) but the partition table was not switched to it. " +
                    (overlapping ? "The partition is marked 'move in progress' so it is not mounted. The data is intact at the new position; only the table entry needs to point there." : "The original partition is intact and still in use.")), ""),
                RelocationPhase.TableUpdateUncertain => (new StepFailure("MOVE_UNCERTAIN", reason + $" The partition table update to {ByteSize.Format(dest)} was issued but its result is unknown. The verified copy is at the new position. Refresh and check the layout before doing anything else."), ""),
                _ => (new StepFailure("MOVE_UNVERIFIED", reason + " The partition table was switched to the verified copy; refresh and check the volume."), ""),
            };
        }
        steps.Add($"Source and destination SHA-256 match ({result.SourceSha256[..16]}...), {result.Chunks} chunks verified");
        steps.Add("Boot sector: " + result.BootSectorUpdate);

        var moved = AwaitPartition(n, x => x.OffsetBytes == dest && x.SizeBytes == src.SizeBytes && (src.UniqueGuid is null || x.UniqueGuid == src.UniqueGuid));
        if (moved is null)
            return (new StepFailure("MOVE_UNVERIFIED", "Windows does not report the partition at its new position after the table update. The data was copied and verified at the new position; refresh and check the layout."), "");

        if (src.DriveLetter is { } letter && moved.DriveLetter != letter)
        {
            // MBR volumes are re-identified by offset, so their drive letter does not follow them automatically.
            try { Step(steps, $"Restore drive letter {letter}:", () => backend.AddDriveLetter(n, moved.PartitionNumber, letter)); }
            catch (StepException ex) { return (new StepFailure("MOVE_LETTER_NOT_RESTORED", $"The partition was moved and verified, but drive letter {letter}: could not be re-assigned ({ex.Message}). Assign it manually."), ""); }
        }
        var mounted = AwaitLayout(n, d => d.Partitions.Any(x => x.OffsetBytes == dest && SameFs(x, src.FileSystem) && x.Label == src.Label &&
                                                               (src.DriveLetter is null || x.DriveLetter == src.DriveLetter)),
            $"The moved volume does not mount as {src.FileSystem} \"{src.Label}\"{(src.DriveLetter is { } l ? $" on {l}:" : "")}.");
        if (mounted is not null) return (new StepFailure("MOVE_MOUNT_UNVERIFIED", mounted.Message + " The blocks were verified by SHA-256; check the volume with chkdsk before use."), "");
        steps.Add("Moved volume mounted with its file system, label and drive letter");

        return (null, $"Moved the {ByteSize.Format(src.SizeBytes)} {src.FileSystem} partition from {ByteSize.Format(src.OffsetBytes)} to {ByteSize.Format(dest)}. " +
                      $"Every sector was copied and verified (SHA-256 {result.SourceSha256[..16]}...); the volume serial number{(src.UniqueGuid is null ? "" : ", partition GUID")} and all file metadata are unchanged.");
    }

    private PartitionInfo? AwaitPartition(int diskNumber, Func<PartitionInfo, bool> match)
    {
        var deadline = DateTime.UtcNow + VerifyTimeout;
        while (true)
        {
            try { if (Discover().Disks.FirstOrDefault(d => d.Number == diskNumber)?.Partitions.FirstOrDefault(match) is { } p) return p; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(250);
        }
    }

    private (StepFailure?, string) Merge(DiskInfo before, PartitionInfo target, PartitionInfo source, List<string> steps)
    {
        var n = before.Number;
        var rollback = new Rollback();
        try
        {
            var tLetter = target.DriveLetter ?? AddTemporaryLetter(n, target, steps, rollback, "target");
            var sLetter = source.DriveLetter ?? AddTemporaryLetter(n, source, steps, rollback, "source");
            var tRoot = Volumes.RootOf(tLetter);
            var sRoot = Volumes.RootOf(sLetter);

            var manifest = Step(steps, "Scan source contents", () => VerifiedCopier.Scan(sRoot));
            if (!manifest.IsSupported)
                return Fail("UNSUPPORTED_CONTENT", "Nothing was changed. The source contains content that cannot be merged faithfully: " + string.Join("; ", manifest.Problems.Take(5)) + ".", rollback);
            if (Volumes.FreeBytes(tRoot) < manifest.TotalBytes + MergeHeadroomBytes)
                return Fail("NOT_ENOUGH_SPACE", $"The target has {ByteSize.Format(Volumes.FreeBytes(tRoot))} free; {ByteSize.Format(manifest.TotalBytes + MergeHeadroomBytes)} is needed. Nothing was changed.", rollback);

            // Conflicts are avoided by construction: everything goes into a new, uniquely named folder.
            var baseName = FileRecoverer.SafeName("Merged from " + (string.IsNullOrWhiteSpace(source.Label) ? $"partition {source.PartitionNumber}" : source.Label));
            var folder = baseName;
            for (var i = 2; Directory.Exists(Path.Combine(tRoot, folder)) || File.Exists(Path.Combine(tRoot, folder)); i++) folder = $"{baseName} ({i})";
            steps.Add($"Source files will go into new folder \"{folder}\" on the target; no existing file is overwritten");

            MakeReadOnly(n, source, sRoot, steps, rollback);
            var destRoot = Path.Combine(tRoot, folder);
            Step(steps, "Create merge folder", () => Directory.CreateDirectory(destRoot));
            rollback.Push("merge folder on target", () => { if (Directory.Exists(destRoot)) DeleteTree(destRoot); });

            var copy = Step(steps, $"Copy {manifest.FileCount} files", () => VerifiedCopier.Copy(manifest, sRoot, destRoot, CancellationToken.None, CopyFaults));
            var errors = Step(steps, "Verify every file (SHA-256)", () => VerifiedCopier.Verify(manifest, copy, destRoot));
            if (errors.Count > 0)
                return Fail("MERGE_VERIFY_FAILED", $"Verification found {errors.Count} problem(s) ({string.Join("; ", errors.Take(3))}). Both partitions are unchanged.", rollback);

            // ---- cutover
            rollback.Commit();
            var adjacentAfter = source.OffsetBytes == target.EndBytes &&
                                !before.Partitions.Any(x => x.OffsetBytes >= target.EndBytes && x.EndBytes <= source.OffsetBytes);
            string spaceNote;
            try
            {
                DeleteSource(n, source, steps);
                if (target.DriveLetter is null) Step(steps, "Unmount temporary target letter", () => backend.RemoveDriveLetter(n, target.PartitionNumber, tLetter));
                if (adjacentAfter && FileSystemRules.CanExtend(target.FileSystem))
                {
                    var (_, max) = Step(steps, "Query extend range", () => backend.GetSupportedSize(n, target.PartitionNumber));
                    var newSize = DiskLayout.AlignDown(Math.Min(max, source.EndBytes - target.OffsetBytes));
                    Step(steps, $"Extend target to {ByteSize.Format(newSize)}", () => backend.Resize(n, target.PartitionNumber, newSize));
                    spaceNote = $"The target was extended into the freed space ({ByteSize.Format(newSize)}).";
                }
                else
                {
                    spaceNote = adjacentAfter
                        ? $"{target.FileSystem} cannot be extended by Windows; the freed space is unallocated."
                        : "The removed partition was not directly after the target, so its space is now unallocated (Windows can only extend a partition into space that follows it).";
                }
            }
            catch (StepException ex)
            {
                return (new StepFailure("MERGE_CUTOVER_INCOMPLETE", ex.Message + $" All files were verified in \"{folder}\" on the target before cutover. " +
                    "Recovery: refresh; if the source partition still exists it can be deleted after re-checking the folder; extend the target manually if desired."), "");
            }

            var landed = AwaitLayout(n, d => !d.Partitions.Any(x => x.OffsetBytes == source.OffsetBytes) && d.Partitions.Any(x => x.UniqueGuid == target.UniqueGuid && x.OffsetBytes == target.OffsetBytes),
                "The final layout still shows the source partition or lost the target.");
            if (landed is not null) return (landed, "");
            if (target.DriveLetter is { } letter)
            {
                var final = VerifiedCopier.Verify(manifest, copy, Path.Combine(Volumes.RootOf(letter), folder));
                if (final.Count > 0) return (new StepFailure("MERGE_FINAL_VERIFY_FAILED", "After cutover: " + string.Join("; ", final.Take(3))), "");
                steps.Add("Merged files re-verified at the final location");
            }
            return (null, $"Merged {manifest.FileCount} files ({ByteSize.Format(copy.BytesCopied)}) into \"{folder}\" and removed the source partition. {spaceNote}");
        }
        catch (StepException ex)
        {
            return Fail(ex.Code == "WRITE_FAILED" ? "MERGE_FAILED" : ex.Code, ex.Message + " Both partitions keep their data.", rollback);
        }
    }

    private char AddTemporaryLetter(int n, PartitionInfo p, List<string> steps, Rollback rollback, string what)
    {
        var letter = FreeLetter();
        Step(steps, $"Mount {what} temporarily as {letter}:", () => backend.AddDriveLetter(n, p.PartitionNumber, letter));
        rollback.Push($"temporary {what} letter", () => backend.RemoveDriveLetter(n, p.PartitionNumber, letter));
        return letter;
    }

    private static void DeleteTree(string path)
    {
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
