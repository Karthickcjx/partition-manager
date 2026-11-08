using PartitionManager.Core.Discovery;
using PartitionManager.Core.Execution;
using PartitionManager.Core.FileSystems;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;
using PartitionManager.Core.Safety;
using PartitionManager.Infrastructure.WindowsApi;

namespace PartitionManager.PrivilegedHelper.Engine;

/// <summary>Writes through documented Windows storage management calls and, for Move only, a locked block-level copy
/// whose partition-table update is performed by Windows (IOCTL_DISK_SET_DRIVE_LAYOUT_EX). No hand-written table bytes.</summary>
public interface IStorageWriteBackend
{
    /// <summary>Pin and independently resolve the attached backing image before any writes.</summary>
    IDisposable? PinBackingImage(DiskInfo disk) => null;
    /// <summary>Sets the file-system label of the volume on the given partition.</summary>
    void SetVolumeLabel(int diskNumber, int partitionNumber, string label);

    /// <summary>Adds the access path "X:\" to the partition.</summary>
    void AddDriveLetter(int diskNumber, int partitionNumber, char letter);

    /// <summary>Removes the access path "X:\" from the partition.</summary>
    void RemoveDriveLetter(int diskNumber, int partitionNumber, char letter);

    /// <summary>The range of sizes Windows can resize the partition to right now (MSFT_Partition.GetSupportedSize).</summary>
    (long Min, long Max) GetSupportedSize(int diskNumber, int partitionNumber);

    /// <summary>Resizes the partition and its file system in place (MSFT_Partition.Resize).</summary>
    void Resize(int diskNumber, int partitionNumber, long sizeBytes);

    /// <summary>Creates a partition at an exact offset and size (MSFT_Disk.CreatePartition). Returns its partition number.</summary>
    int CreatePartition(int diskNumber, long offsetBytes, long sizeBytes);

    /// <summary>Formats the volume on the partition (MSFT_Volume.Format).</summary>
    void Format(int diskNumber, int partitionNumber, string fileSystem, string label, int clusterSize, bool full);

    /// <summary>Deletes the partition (MSFT_Partition.DeleteObject).</summary>
    void DeletePartition(int diskNumber, int partitionNumber);

    /// <summary>Writes an empty MBR or GPT partition table to an uninitialized disk (MSFT_Disk.Initialize).</summary>
    void InitializeDisk(int diskNumber, PartitionStyle style);

    /// <summary>Sets or clears the partition's read-only attribute and remounts it so the volume honours it.</summary>
    void SetPartitionReadOnly(int diskNumber, int partitionNumber, bool readOnly);

    /// <summary>
    /// Block-level move of a partition to a new start offset (same size): exclusive volume lock + dismount, overlap-safe
    /// chunked copy with read-back verification, full-extent SHA-256 comparison, boot-sector start fields updated, then
    /// the partition entry is rewritten through IOCTL_DISK_SET_DRIVE_LAYOUT_EX. <paramref name="checkpoint"/> receives
    /// (chunk offset, bytes copied) after each verified chunk.
    /// </summary>
    RelocationResult RelocatePartition(int diskNumber, PartitionTarget source, long newOffsetBytes, Action<long, long>? checkpoint) =>
        throw new StorageWriteException("Block-level relocation is not available in this backend.");
}

/// <summary>How the engine reaches mounted volumes' files. Overridden in tests to map letters to temp folders.</summary>
public class VolumeAccess
{
    public virtual string RootOf(char letter) => $"{char.ToUpperInvariant(letter)}:\\";

    /// <summary>True if a file can be created on the volume (used to prove a source really is read-only).</summary>
    public virtual bool CanWrite(string root)
    {
        var probe = Path.Combine(root, $".pm-write-probe-{Guid.NewGuid():N}");
        try { File.WriteAllBytes(probe, []); File.Delete(probe); return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return false; }
    }

    public virtual long FreeBytes(string root) => new DriveInfo(root).AvailableFreeSpace;
}

public sealed class StorageWriteException(string message) : Exception(message);

public interface IAuditSink
{
    void Record(string requestId, string kind, string target, string outcome, string detail);
}

/// <summary>
/// Server-side execution pipeline. The UI already validated everything, but none of that is trusted here:
/// VALIDATE → POLICY → REDISCOVER → IDENTITY → TARGET → GATE → PRECONDITIONS → SAFETY → EXECUTE → VERIFY → AUDIT.
/// Structural operations additionally verify that every other partition on the disk is unchanged.
/// </summary>
public sealed partial class WriteEngine(IDiskDiscoveryService discovery, IStorageWriteBackend backend, IHelperPolicySource policySource, IAuditSink audit, VolumeAccess? volumeAccess = null)
{
    private readonly SafetyPolicy _safety = new();
    private readonly object _gate = new();
    public IOperationJournal? Journal { get; init; }
    // Only the friend test assembly exercises the inherited, unreleased migration prototype.
    // No IPC, policy, environment variable or command-line setting can enable it.
    internal bool ExerciseUnreleasedStagingForTests { get; init; }

    /// <summary>Windows' storage provider can take a moment to report a new layout; verification polls up to this long.</summary>
    public TimeSpan VerifyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public HelperResponse Execute(Guid requestId, WriteOperationRequest request)
    {
        lock (_gate)
        {
            var steps = new List<string>();
            var target = $"Disk {request?.Disk?.Number} partition {request?.Partition?.PartitionNumber.ToString() ?? "(new)"}";
            HelperResponse Reject(string code, string message)
            {
                audit.Record(requestId.ToString(), request?.Kind.ToString() ?? "?", target, "REJECTED " + code, message);
                return new HelperResponse(requestId, HelperResponseStatus.Rejected, code, message, steps);
            }

            var v = WriteRequestValidator.Validate(request);
            if (!v.IsValid) return Reject(v.Code, v.Message);
            steps.Add("Request validated");

            var policy = policySource.Load();
            if (policy.Mode == WriteMode.Disabled) return Reject("WRITES_DISABLED", "Real disk writes are disabled by the helper policy.");

            PreviewState state;
            try { state = Discover(); }
            catch (Exception ex) { return Reject("DISCOVERY_FAILED", "Could not re-enumerate disks: " + ex.Message); }
            steps.Add("Disks re-enumerated");

            var match = DiskIdentityMatcher.Match(request!.Disk, state.Disks);
            if (!match.IsMatch) return Reject("IDENTITY_" + match.Status.ToString().ToUpperInvariant(), match.Message);
            var disk = match.Disk!;
            steps.Add($"Disk identity confirmed ({disk.Model}, S/N {disk.SerialNumber})");

            PartitionInfo? partition = null;
            if (request.Partition is not null)
            {
                partition = disk.Partitions.FirstOrDefault(request.Partition.Matches);
                if (partition is null) return Reject("PARTITION_CHANGED", "The target partition no longer matches (number, offset, size or GUID differ).");
            }
            PartitionInfo? second = null;
            if (request.SecondPartition is not null)
            {
                second = disk.Partitions.FirstOrDefault(request.SecondPartition.Matches);
                if (second is null) return Reject("PARTITION_CHANGED", "The second partition no longer matches (number, offset, size or GUID differ).");
            }

            // Release gate: only attached virtual disks whose backing file sits in an approved folder.
            IDisposable? imageLease = null;
            if (!disk.IsVirtual)
            {
                // Test machines only: an administrator-enrolled physical disk, in-place shrink/extend via MSFT_Partition.Resize.
                if (!policy.AllowsPhysicalResize(disk, request.Kind))
                    return Reject("PHYSICAL_DISK", $"{disk.Title} is a physical disk. Physical-disk writes are not enabled in this version" +
                                  (policy.PhysicalTestDiskIds.Count > 0 ? " (an enrolled test disk allows only in-place shrink and extend)." : "."));
                if (!string.IsNullOrEmpty(request.Disk.VirtualImagePath)) return Reject("IMAGE_MISMATCH", "The request names a virtual disk image for a physical disk.");
                steps.Add("Enrolled physical test disk: in-place resize gate passed");
            }
            else
            {
                if (!policy.AllowsImage(disk.VirtualImagePath)) return Reject("IMAGE_NOT_APPROVED", $"The virtual disk image \"{disk.VirtualImagePath}\" is not in an approved test folder.");
                if (string.IsNullOrEmpty(request.Disk.VirtualImagePath) || !string.Equals(Path.GetFullPath(request.Disk.VirtualImagePath), Path.GetFullPath(disk.VirtualImagePath), StringComparison.OrdinalIgnoreCase))
                    return Reject("IMAGE_MISMATCH", "The request names a different virtual disk image than the one attached.");
                steps.Add("Virtual-disk release gate passed");
                try { imageLease = backend.PinBackingImage(disk); }
                catch (Exception ex) { return Reject("IMAGE_IDENTITY_FAILED", "Cannot independently verify the backing image: " + ex.Message); }
            }
            using var pinnedImage = imageLease;

            if (!ExerciseUnreleasedStagingForTests && request.Kind is WriteOperationKind.MergePartitions)
                return Reject("UNSUPPORTED_OPERATION", "Merge is simulation only: consistent copying, metadata preservation and durable recovery have not been proven. No changes were made.");

            if (request.Kind == WriteOperationKind.InitializeDisk && !policy.ApprovesInitialization(disk, DateTimeOffset.UtcNow))
                return Reject("INITIALIZATION_NOT_APPROVED", "Initialization requires a fresh administrator-issued approval for this newly created blank VHDX identity and layout.");

            if (!request.IsStructural && partition is { HasFileSystem: false }) return Reject("NO_VOLUME", "The partition has no mounted file system.");
            var precondition = CheckPreconditions(request, partition, state);
            if (precondition.IsValid && request.Kind is WriteOperationKind.InitializeDisk or WriteOperationKind.MovePartition or WriteOperationKind.MergePartitions)
                precondition = CheckStagedPreconditions(request, disk, partition, second);
            if (!precondition.IsValid) return Reject(precondition.Code, precondition.Message);

            var op = ToOperation(request, disk, partition);
            var verdict = _safety.Evaluate(op, state);
            if (!verdict.IsAllowed) return Reject("SAFETY_POLICY", verdict.Blockers[0]);
            // Move and Merge are validated by CheckStagedPreconditions; the simulator's model of them (in-place move,
            // adjacent-only merge) is narrower than the staged implementation.
            if (request.Kind is not (WriteOperationKind.MovePartition or WriteOperationKind.MergePartitions))
            {
                try { op.Apply(state); }
                catch (OperationValidationException ex) { return Reject("INVALID_OPERATION", ex.Message); }
            }
            steps.Add("Safety policy passed");

            // Revalidate after potentially slow size/volume checks, immediately before dispatch.
            var fresh = DiskIdentityMatcher.Match(request.Disk, Discover().Disks);
            if (!fresh.IsMatch) return Reject("LAYOUT_CHANGED_BEFORE_WRITE", "Target changed during preparation; refresh and confirm again.");
            var originalBackend = backend;
            var begun = false;
            DiskInfo? Observe() { try { return Discover().Disks.FirstOrDefault(d => d.Number == disk.Number); } catch { return null; } }
            try
            {
                if (Journal is not null)
                {
                    DiskInfo intended;
                    // The simulator models moves only within the partition's neighbouring free space; a jump into
                    // another free segment is still valid for the block mover, so fall back to the current layout.
                    try { intended = op.Apply(state).Disk(disk.Number); }
                    catch (OperationValidationException) when (request.Kind == WriteOperationKind.MovePartition) { intended = disk; }
                    Journal.Begin(requestId, request, disk, intended);
                    begun = true;
                    backend = new JournaledBackend(originalBackend, Journal, Observe, disk);
                }
                audit.Record(requestId.ToString(), request.Kind.ToString(), Describe(disk, partition), "STARTED", DescribeChange(request));
                var response = request.IsStructural
                    ? ExecuteStructural(requestId, request, disk, partition, second, steps)
                    : ExecuteMetadata(requestId, request, disk, partition!, steps);
                Journal?.Record(response.Status == HelperResponseStatus.Ok ? "Verified" : response.Code == MoveNotStarted ? "NotStarted" : "Failed",
                    response.Code + ": " + response.Message, Observe());
                return response;
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
            {
                // A journal/audit error after dispatch cannot be converted into success.
                // The incomplete durable intent remains a restart barrier.
                return new HelperResponse(requestId, begun ? HelperResponseStatus.Failed : HelperResponseStatus.Rejected,
                    begun ? "OPERATION_UNCERTAIN" : "JOURNAL_OR_AUDIT_UNAVAILABLE",
                    ex.Message + " " + FileOperationJournal.RecoveryInstructions, steps);
            }
            finally { backend = originalBackend; }
        }
    }

    private PreviewState Discover() => discovery.DiscoverAsync().GetAwaiter().GetResult().State;

    // ---------------------------------------------------------------- metadata (label, letters)

    private HelperResponse ExecuteMetadata(Guid requestId, WriteOperationRequest request, DiskInfo disk, PartitionInfo partition, List<string> steps)
    {
        try
        {
            switch (request.Kind)
            {
                case WriteOperationKind.SetVolumeLabel:
                    backend.SetVolumeLabel(partition.DiskNumber, partition.PartitionNumber, request.NewLabel!);
                    break;
                case WriteOperationKind.AssignDriveLetter:
                    backend.AddDriveLetter(partition.DiskNumber, partition.PartitionNumber, request.NewDriveLetter!.Value);
                    break;
                case WriteOperationKind.RemoveDriveLetter:
                    backend.RemoveDriveLetter(partition.DiskNumber, partition.PartitionNumber, request.ExpectedCurrentDriveLetter!.Value);
                    break;
                case WriteOperationKind.ChangeDriveLetter:
                    backend.RemoveDriveLetter(partition.DiskNumber, partition.PartitionNumber, request.ExpectedCurrentDriveLetter!.Value);
                    backend.AddDriveLetter(partition.DiskNumber, partition.PartitionNumber, request.NewDriveLetter!.Value);
                    break;
            }
        }
        catch (Exception ex) when (IsWriteFailure(ex))
        {
            var message = ex.Message;
            if (request.Kind == WriteOperationKind.ChangeDriveLetter)
                message += TryRestoreLetter(request, partition);
            audit.Record(requestId.ToString(), request.Kind.ToString(), Describe(disk, partition), "FAILED", message);
            return new HelperResponse(requestId, HelperResponseStatus.Failed, "WRITE_FAILED", message, steps);
        }
        steps.Add("Change written");

        var verification = VerifyMetadata(request);
        audit.Record(requestId.ToString(), request.Kind.ToString(), Describe(disk, partition), verification.IsValid ? "SUCCEEDED" : "UNVERIFIED", verification.Message);
        if (!verification.IsValid)
            return new HelperResponse(requestId, HelperResponseStatus.Failed, verification.Code, verification.Message, steps);
        steps.Add("Result verified");
        return new HelperResponse(requestId, HelperResponseStatus.Ok, "OK", DescribeChange(request), steps);
    }

    // ---------------------------------------------------------------- structural

    private sealed record StepFailure(string Code, string Message);

    private HelperResponse ExecuteStructural(Guid requestId, WriteOperationRequest request, DiskInfo before, PartitionInfo? partition, PartitionInfo? second, List<string> steps)
    {
        var n = before.Number;
        // Everything this operation is allowed to touch; all other partitions must be byte-for-byte unchanged afterwards.
        var others = before.Partitions.Where(p => (partition is null || !request.Partition!.Matches(p)) && (second is null || !request.SecondPartition!.Matches(p)))
            .Select(Geometry).ToList();

        StepFailure? failure = null;
        string summary;
        try
        {
            switch (request.Kind)
            {
                case WriteOperationKind.CreatePartition:
                {
                    var pn = Step(steps, "Create partition", () => backend.CreatePartition(n, request.OffsetBytes!.Value, request.SizeBytes!.Value));
                    Step(steps, "Format new partition", () => backend.Format(n, pn, request.FileSystem!, request.NewLabel ?? "", 0, full: false));
                    if (request.NewDriveLetter is { } letter) Step(steps, "Assign drive letter", () => backend.AddDriveLetter(n, pn, letter));
                    failure = AwaitLayout(n, d => d.Partitions.Any(p => p.OffsetBytes == request.OffsetBytes && SameFs(p, request.FileSystem!) && p.Label == (request.NewLabel ?? "")
                                                                    && (request.NewDriveLetter is null || p.DriveLetter == request.NewDriveLetter)),
                        "The new partition was not found with the requested file system, label and letter.");
                    summary = $"Created a {ByteSize.Format(request.SizeBytes!.Value)} {request.FileSystem} partition at {ByteSize.Format(request.OffsetBytes!.Value)}.";
                    break;
                }
                case WriteOperationKind.FormatVolume:
                    Step(steps, "Format volume", () => backend.Format(n, partition!.PartitionNumber, request.FileSystem!, request.NewLabel ?? "", request.ClusterSize ?? 0, !request.QuickFormat));
                    failure = AwaitLayout(n, d => d.Partitions.Any(p => request.Partition!.Matches(p) && SameFs(p, request.FileSystem!) && p.Label == (request.NewLabel ?? "")),
                        "The volume does not report the requested file system and label.");
                    summary = $"Formatted as {request.FileSystem}.";
                    break;

                case WriteOperationKind.DeletePartition:
                    Step(steps, "Delete partition", () => backend.DeletePartition(n, partition!.PartitionNumber));
                    failure = AwaitLayout(n, d => !d.Partitions.Any(p => p.OffsetBytes == partition!.OffsetBytes || (p.UniqueGuid is not null && p.UniqueGuid == partition.UniqueGuid)),
                        "The partition is still present.");
                    summary = $"Deleted partition {partition!.PartitionNumber}.";
                    break;

                case WriteOperationKind.ShrinkPartition:
                case WriteOperationKind.ExtendPartition:
                    Step(steps, request.Kind == WriteOperationKind.ShrinkPartition ? "Shrink partition" : "Extend partition",
                        () => backend.Resize(n, partition!.PartitionNumber, request.SizeBytes!.Value));
                    failure = AwaitLayout(n, d => d.Partitions.Any(p => p.UniqueGuid == partition!.UniqueGuid && p.OffsetBytes == partition.OffsetBytes && p.SizeBytes == request.SizeBytes),
                        $"The partition does not report the requested size of {ByteSize.Format(request.SizeBytes!.Value)}.");
                    summary = $"Resized from {ByteSize.Format(partition!.SizeBytes)} to {ByteSize.Format(request.SizeBytes!.Value)}.";
                    break;

                case WriteOperationKind.SplitPartition:
                    (failure, summary) = Split(request, before, partition!, steps);
                    break;

                case WriteOperationKind.InitializeDisk:
                    Step(steps, $"Initialize as {request.NewStyle}", () => backend.InitializeDisk(n, request.NewStyle!.Value));
                    failure = AwaitLayout(n, d => d.Style == request.NewStyle && d.Partitions.Count(p => p.Role != PartitionRole.MicrosoftReserved) == 0,
                        $"The disk does not report the {request.NewStyle} partition style.");
                    summary = $"Initialized as {request.NewStyle}.";
                    break;

                case WriteOperationKind.MovePartition:
                    (failure, summary) = Move(request, before, partition!, steps);
                    break;

                case WriteOperationKind.MergePartitions:
                    (failure, summary) = Merge(before, partition!, second!, steps);
                    break;

                default:
                    throw new InvalidOperationException("Unhandled kind " + request.Kind);
            }
        }
        catch (StepException ex)
        {
            failure = new StepFailure(ex.Code, ex.Message);
            summary = "";
        }

        // Independent check: nothing outside the operation's scope changed.
        PreviewState after;
        try { after = Discover(); }
        catch (Exception ex)
        {
            var msg = "The disk could not be re-read after the operation: " + ex.Message + " Refresh before doing anything else.";
            audit.Record(requestId.ToString(), request.Kind.ToString(), Describe(before, partition), "UNVERIFIED", msg);
            return new HelperResponse(requestId, HelperResponseStatus.Failed, "VERIFY_FAILED", msg, steps);
        }
        var afterDisk = after.Disks.FirstOrDefault(d => d.Number == n);
        if (afterDisk is null)
        {
            var msg = $"The disk disappeared during the operation (last step: {steps.LastOrDefault()}). Its state is unknown; reconnect it and refresh before doing anything else."
                      + (failure is null ? "" : " " + failure.Message);
            audit.Record(requestId.ToString(), request.Kind.ToString(), Describe(before, partition), "DISK_DISCONNECTED", msg);
            return new HelperResponse(requestId, HelperResponseStatus.Failed, "DISK_DISCONNECTED", msg, steps);
        }
        if (afterDisk.IsVirtual != before.IsVirtual || afterDisk.SizeBytes != before.SizeBytes || afterDisk.SerialNumber != before.SerialNumber ||
            !string.Equals(afterDisk.VirtualImagePath, before.VirtualImagePath, StringComparison.OrdinalIgnoreCase) ||
            (request.Kind != WriteOperationKind.InitializeDisk && afterDisk.UniqueId != before.UniqueId))
        {
            const string message = "The disk identity changed during execution. The result is unverified; stop and inspect both devices.";
            audit.Record(requestId.ToString(), request.Kind.ToString(), Describe(before, partition), "UNVERIFIED", message);
            return new HelperResponse(requestId, HelperResponseStatus.Failed, "DISK_REPLACED", message, steps);
        }
        var stillThere = afterDisk?.Partitions.Select(Geometry).ToHashSet() ?? [];
        var changedOthers = others.Where(o => !stillThere.Contains(o)).ToList();
        var layout = afterDisk is null ? "Disk is no longer present." : LayoutText(afterDisk);

        if (changedOthers.Count > 0)
        {
            var msg = $"UNEXPECTED: {changedOthers.Count} partition(s) outside the operation's scope changed or disappeared ({string.Join("; ", changedOthers)}). Current layout: {layout}. Stop and investigate before any further change.";
            audit.Record(requestId.ToString(), request.Kind.ToString(), Describe(before, partition), "UNEXPECTED_CHANGE", msg);
            return new HelperResponse(requestId, HelperResponseStatus.Failed, "OTHER_PARTITIONS_CHANGED", msg, steps);
        }
        steps.Add("Other partitions unchanged");

        if (failure is not null)
        {
            var msg = $"{failure.Message} Current layout: {layout}.";
            audit.Record(requestId.ToString(), request.Kind.ToString(), Describe(before, partition), "FAILED " + failure.Code, msg);
            return new HelperResponse(requestId, HelperResponseStatus.Failed, failure.Code, msg, steps);
        }
        steps.Add("Result verified");
        audit.Record(requestId.ToString(), request.Kind.ToString(), Describe(before, partition), "SUCCEEDED", summary + " Layout: " + layout);
        return new HelperResponse(requestId, HelperResponseStatus.Ok, "OK", summary, steps);
    }

    /// <summary>
    /// Split = shrink → verify freed space → create in exactly that space → format → letter → verify. Not atomic: if a
    /// step fails the sequence stops and the response names the reached state and how to recover from it.
    /// </summary>
    private (StepFailure? Failure, string Summary) Split(WriteOperationRequest request, DiskInfo before, PartitionInfo p, List<string> steps)
    {
        var n = before.Number;
        var first = request.SizeBytes!.Value;
        var originalEnd = p.EndBytes;

        try { Step(steps, "Shrink original partition", () => backend.Resize(n, p.PartitionNumber, first)); }
        catch (StepException ex)
        {
            return (new StepFailure("SPLIT_SHRINK_FAILED", ex.Message + " The shrink call failed; it may have changed the partition. Inspect the current layout below and verify files before retrying."), "");
        }

        var shrunk = AwaitLayout(n, d => d.Partitions.Any(x => x.UniqueGuid == p.UniqueGuid && x.OffsetBytes == p.OffsetBytes && x.SizeBytes == first),
            "The original partition does not report the requested size after shrinking.");
        if (shrunk is not null)
            return (new StepFailure("SPLIT_SHRINK_UNVERIFIED", shrunk.Message + " No new partition was created. Refresh and check the partition's size; extend it back if needed."), "");

        var newOffset = DiskLayout.AlignUp(p.OffsetBytes + first);
        var newSize = DiskLayout.AlignDown(originalEnd - newOffset);
        var current = Discover().Disk(n);
        var freed = DiskLayout.Segments(current, DiskLayout.Alignment).FirstOrDefault(s => s.IsUnallocated && s.OffsetBytes <= newOffset && s.EndBytes >= newOffset + newSize);
        if (freed is null || newSize < DiskLayout.Alignment)
            return (new StepFailure("SPLIT_NO_FREE_SPACE", $"The partition was shrunk to {ByteSize.Format(first)}, but the expected free space at {ByteSize.Format(newOffset)} was not found. " +
                                     "No new partition was created. Recovery: extend the original partition back to its previous size."), "");
        steps.Add($"Freed space verified ({ByteSize.Format(newSize)} at {ByteSize.Format(newOffset)})");

        int pn;
        try { pn = Step(steps, "Create new partition in freed space", () => backend.CreatePartition(n, newOffset, newSize)); }
        catch (StepException ex)
        {
            return (new StepFailure("SPLIT_CREATE_FAILED", ex.Message + $" The original partition is now {ByteSize.Format(first)} and its data is intact; " +
                                     $"{ByteSize.Format(newSize)} is unallocated after it. Recovery: retry Create in that space, or extend the original partition back to {ByteSize.Format(p.SizeBytes)}."), "");
        }

        try { Step(steps, "Format new partition", () => backend.Format(n, pn, request.FileSystem!, request.NewLabel ?? "", 0, full: false)); }
        catch (StepException ex)
        {
            return (new StepFailure("SPLIT_FORMAT_FAILED", ex.Message + $" The new partition exists but is not formatted (RAW). The original partition ({ByteSize.Format(first)}) is intact. " +
                                     "Recovery: format the new partition, or delete it and extend the original back."), "");
        }

        if (request.NewDriveLetter is { } letter)
        {
            try { Step(steps, "Assign drive letter", () => backend.AddDriveLetter(n, pn, letter)); }
            catch (StepException ex)
            {
                return (new StepFailure("SPLIT_LETTER_FAILED", ex.Message + $" Both partitions exist and are formatted; the new one has no drive letter. Recovery: assign a letter to it."), "");
            }
        }

        var verified = AwaitLayout(n, d =>
                d.Partitions.Any(x => x.UniqueGuid == p.UniqueGuid && x.SizeBytes == first) &&
                d.Partitions.Any(x => x.OffsetBytes == newOffset && SameFs(x, request.FileSystem!) && (request.NewDriveLetter is null || x.DriveLetter == request.NewDriveLetter)),
            "The final layout does not match the planned split.");
        if (verified is not null) return (new StepFailure("SPLIT_UNVERIFIED", verified.Message), "");
        return (null, $"Split into {ByteSize.Format(first)} and {ByteSize.Format(newSize)} ({request.FileSystem}{(request.NewDriveLetter is { } l ? $", {l}:" : "")}).");
    }

    /// <summary>Failure code used only when the relocation's own progress proves that no write was issued.</summary>
    internal const string MoveNotStarted = "MOVE_NOT_STARTED";

    private sealed class StepException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private static T Step<T>(List<string> steps, string name, Func<T> action)
    {
        try
        {
            var result = action();
            steps.Add(name);
            return result;
        }
        // Any exception mid-sequence (WMI, COM, device removal, provider bugs) must become a reported, stopped step,
        // never an unhandled crash that leaves the caller without the resulting layout.
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            throw new StepException("WRITE_FAILED", $"{name} failed: {ex.Message}.");
        }
    }

    private static void Step(List<string> steps, string name, Action action) => Step(steps, name, () => { action(); return 0; });

    private StepFailure? AwaitLayout(int diskNumber, Func<DiskInfo, bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + VerifyTimeout;
        while (true)
        {
            try
            {
                var d = Discover().Disks.FirstOrDefault(x => x.Number == diskNumber);
                if (d is not null && condition(d)) return null;
                if (d is null) return new StepFailure("DISK_DISCONNECTED", "The disk disappeared during the operation.");
            }
            catch (Exception) when (DateTime.UtcNow < deadline) { }
            if (DateTime.UtcNow >= deadline) return new StepFailure("VERIFY_FAILED", message);
            Thread.Sleep(250);
        }
    }

    private static bool IsWriteFailure(Exception ex) => ex is StorageWriteException or System.Management.ManagementException or UnauthorizedAccessException or IOException;

    private static bool SameFs(PartitionInfo p, string fs) => string.Equals(p.FileSystem, FileSystemRules.Normalize(fs), StringComparison.OrdinalIgnoreCase);

    private static string Geometry(PartitionInfo p) => $"#{p.PartitionNumber}@{p.OffsetBytes}+{p.SizeBytes}{(p.UniqueGuid is { } g ? " " + g : "")}";

    private static string LayoutText(DiskInfo d) => string.Join(", ", d.Partitions.Select(p =>
        $"#{p.PartitionNumber} {ByteSize.Format(p.SizeBytes)} at {ByteSize.Format(p.OffsetBytes)} {(p.HasFileSystem ? p.FileSystem : "RAW")}{(p.DriveLetter is { } c ? $" {c}:" : "")}"));

    // ---------------------------------------------------------------- preconditions and mapping

    private ValidationResult CheckPreconditions(WriteOperationRequest r, PartitionInfo? p, PreviewState state)
    {
        switch (r.Kind)
        {
            case WriteOperationKind.SetVolumeLabel:
                return WriteRequestValidator.ValidateLabelFor(p!.FileSystem, r.NewLabel!);
            case WriteOperationKind.AssignDriveLetter:
                if (p!.DriveLetter is not null) return ValidationResult.Fail("STATE_CHANGED", $"The partition already has drive letter {p.DriveLetter}:.");
                break;
            case WriteOperationKind.RemoveDriveLetter:
            case WriteOperationKind.ChangeDriveLetter:
                if (p!.DriveLetter != r.ExpectedCurrentDriveLetter)
                    return ValidationResult.Fail("STATE_CHANGED", $"The partition's drive letter is {(p.DriveLetter is { } c ? c + ":" : "none")}, not {r.ExpectedCurrentDriveLetter}:.");
                break;
            case WriteOperationKind.ShrinkPartition:
            case WriteOperationKind.ExtendPartition:
            case WriteOperationKind.SplitPartition:
            {
                var shrinking = r.Kind != WriteOperationKind.ExtendPartition;
                if (shrinking && !FileSystemRules.CanShrink(p!.FileSystem) || !p!.HasFileSystem)
                    return ValidationResult.Fail("UNSUPPORTED_FILE_SYSTEM", $"{(p.HasFileSystem ? p.FileSystem : "Unformatted")} partitions cannot be {(shrinking ? "shrunk or split" : "extended")} by Windows.");
                if (!shrinking && !FileSystemRules.CanExtend(p.FileSystem))
                    return ValidationResult.Fail("UNSUPPORTED_FILE_SYSTEM", $"{p.FileSystem} partitions cannot be extended by Windows.");
                (long min, long max) supported;
                try { supported = backend.GetSupportedSize(p.DiskNumber, p.PartitionNumber); }
                catch (Exception ex) when (IsWriteFailure(ex)) { return ValidationResult.Fail("SUPPORTED_SIZE_FAILED", "Windows could not report the supported sizes: " + ex.Message); }
                var size = r.SizeBytes!.Value;
                if (size < supported.min)
                    return ValidationResult.Fail("BELOW_MINIMUM", $"Windows can shrink this partition to {ByteSize.Format(supported.min)} at most (files or unmovable data occupy the rest); {ByteSize.Format(size)} was requested.");
                if (size > supported.max)
                    return ValidationResult.Fail("NOT_ENOUGH_SPACE", $"There is room to grow this partition to {ByteSize.Format(supported.max)}; {ByteSize.Format(size)} was requested.");
                break;
            }
            case WriteOperationKind.CreatePartition:
            {
                var disk = state.Disk(r.Disk.Number);
                var end = r.OffsetBytes!.Value + r.SizeBytes!.Value;
                if (!DiskLayout.FreeSegments(disk).Any(s => s.OffsetBytes <= r.OffsetBytes && end <= s.EndBytes))
                    return ValidationResult.Fail("NOT_ENOUGH_SPACE", "The requested range is not entirely inside unallocated space.");
                break;
            }
        }
        if (r.NewDriveLetter is { } n && (state.UsedLetters.Contains(n) || DriveLetterInUse(n)))
            return ValidationResult.Fail("LETTER_IN_USE", $"Drive letter {n}: is in use.");
        return ValidationResult.Ok;
    }

    private static bool DriveLetterInUse(char letter) =>
        DriveInfo.GetDrives().Any(d => char.ToUpperInvariant(d.Name[0]) == char.ToUpperInvariant(letter));

    private static DiskOperation ToOperation(WriteOperationRequest r, DiskInfo disk, PartitionInfo? p) => r.Kind switch
    {
        WriteOperationKind.SetVolumeLabel => new ChangeLabelOperation(disk.Number, p!.PartitionNumber, r.NewLabel!),
        WriteOperationKind.RemoveDriveLetter => new ChangeDriveLetterOperation(disk.Number, p!.PartitionNumber, null),
        WriteOperationKind.AssignDriveLetter or WriteOperationKind.ChangeDriveLetter => new ChangeDriveLetterOperation(disk.Number, p!.PartitionNumber, r.NewDriveLetter),
        WriteOperationKind.CreatePartition => new CreatePartitionOperation(disk.Number, r.OffsetBytes!.Value, r.SizeBytes!.Value, r.FileSystem!, r.NewLabel ?? "", r.NewDriveLetter),
        WriteOperationKind.FormatVolume => new FormatPartitionOperation(disk.Number, p!.PartitionNumber, r.FileSystem!, r.NewLabel ?? "", r.ClusterSize ?? 0, r.QuickFormat),
        WriteOperationKind.DeletePartition => new DeletePartitionOperation(disk.Number, p!.PartitionNumber),
        WriteOperationKind.ShrinkPartition or WriteOperationKind.ExtendPartition => new ResizeMovePartitionOperation(disk.Number, p!.PartitionNumber, p.OffsetBytes, r.SizeBytes!.Value),
        WriteOperationKind.SplitPartition => new SplitPartitionOperation(disk.Number, p!.PartitionNumber, r.SizeBytes!.Value, r.FileSystem!, r.NewLabel ?? "", r.NewDriveLetter),
        WriteOperationKind.InitializeDisk => new InitializeDiskOperation(disk.Number, r.NewStyle!.Value),
        WriteOperationKind.MovePartition => new ResizeMovePartitionOperation(disk.Number, p!.PartitionNumber, r.OffsetBytes!.Value, p.SizeBytes),
        WriteOperationKind.MergePartitions => new MergePartitionsOperation(disk.Number, p!.PartitionNumber, r.SecondPartition!.PartitionNumber),
        _ => throw new InvalidOperationException("Unhandled kind " + r.Kind),
    };

    /// <summary>A letter change is two calls; if the second fails, try to put the old letter back.</summary>
    private string TryRestoreLetter(WriteOperationRequest r, PartitionInfo p)
    {
        try
        {
            var current = Discover().Disks.FirstOrDefault(d => d.Number == p.DiskNumber)?.Partitions.FirstOrDefault(r.Partition!.Matches);
            if (current is { DriveLetter: null })
            {
                backend.AddDriveLetter(p.DiskNumber, p.PartitionNumber, r.ExpectedCurrentDriveLetter!.Value);
                return $" The original letter {r.ExpectedCurrentDriveLetter}: was restored.";
            }
            return "";
        }
        catch (Exception ex)
        {
            return $" Restoring the original letter {r.ExpectedCurrentDriveLetter}: also failed ({ex.Message}); the partition may have no drive letter.";
        }
    }

    private ValidationResult VerifyMetadata(WriteOperationRequest r)
    {
        PreviewState after;
        try { after = Discover(); }
        catch (Exception ex) { return ValidationResult.Fail("VERIFY_FAILED", "The change was sent but could not be verified: " + ex.Message); }
        var p = after.Disks.FirstOrDefault(d => d.Number == r.Disk.Number)?.Partitions.FirstOrDefault(r.Partition!.Matches);
        if (p is null) return ValidationResult.Fail("VERIFY_FAILED", "The partition could not be found after the change.");
        return r.Kind switch
        {
            WriteOperationKind.SetVolumeLabel when p.Label != r.NewLabel =>
                ValidationResult.Fail("VERIFY_FAILED", $"The label reads \"{p.Label}\" after the change, expected \"{r.NewLabel}\"."),
            WriteOperationKind.AssignDriveLetter or WriteOperationKind.ChangeDriveLetter when p.DriveLetter != r.NewDriveLetter =>
                ValidationResult.Fail("VERIFY_FAILED", $"The drive letter reads {p.DriveLetter?.ToString() ?? "none"} after the change, expected {r.NewDriveLetter}."),
            WriteOperationKind.RemoveDriveLetter when p.DriveLetter is not null =>
                ValidationResult.Fail("VERIFY_FAILED", $"The partition still has drive letter {p.DriveLetter}:."),
            _ => ValidationResult.Ok,
        };
    }

    private static string Describe(DiskInfo d, PartitionInfo? p) =>
        $"Disk {d.Number} [{d.Model}, S/N {d.SerialNumber}, image {d.VirtualImagePath}] " + (p is null ? "unallocated space" : $"partition {p.PartitionNumber} @ {p.OffsetBytes}");

    private static string DescribeChange(WriteOperationRequest r) => r.Kind switch
    {
        WriteOperationKind.SetVolumeLabel => $"Label set to \"{r.NewLabel}\".",
        WriteOperationKind.AssignDriveLetter => $"Drive letter {r.NewDriveLetter}: assigned.",
        WriteOperationKind.RemoveDriveLetter => $"Drive letter {r.ExpectedCurrentDriveLetter}: removed.",
        WriteOperationKind.ChangeDriveLetter => $"Drive letter changed from {r.ExpectedCurrentDriveLetter}: to {r.NewDriveLetter}:.",
        WriteOperationKind.CreatePartition => $"Create {ByteSize.Format(r.SizeBytes ?? 0)} {r.FileSystem} at {ByteSize.Format(r.OffsetBytes ?? 0)}.",
        WriteOperationKind.FormatVolume => $"Format as {r.FileSystem}.",
        WriteOperationKind.DeletePartition => "Delete partition.",
        WriteOperationKind.ShrinkPartition => $"Shrink to {ByteSize.Format(r.SizeBytes ?? 0)}.",
        WriteOperationKind.ExtendPartition => $"Extend to {ByteSize.Format(r.SizeBytes ?? 0)}.",
        _ => $"Split keeping {ByteSize.Format(r.SizeBytes ?? 0)}, new {r.FileSystem} partition.",
    };
}
