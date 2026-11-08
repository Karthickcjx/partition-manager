using System.Text.Json.Serialization;
using PartitionManager.Core.FileSystems;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;

namespace PartitionManager.Core.Execution;

/// <summary>The complete allowlist of write operations the privileged helper accepts. Anything else is rejected.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WriteOperationKind>))]
public enum WriteOperationKind
{
    SetVolumeLabel, AssignDriveLetter, RemoveDriveLetter, ChangeDriveLetter,
    CreatePartition, FormatVolume, DeletePartition, ShrinkPartition, ExtendPartition, SplitPartition,
    InitializeDisk, MovePartition, MergePartitions,
}

/// <summary>Whether, and when, an operation can be interrupted without leaving a partial change behind.</summary>
public enum CancellationSemantics
{
    /// <summary>Can be cancelled while queued; once started it runs to completion (short, single metadata call).</summary>
    BeforeExecutionOnly,
    /// <summary>Can stop at defined checkpoints; work before the checkpoint stays applied.</summary>
    AtCheckpoints,
    /// <summary>Must not be interrupted after commitment; interruption may leave the target unusable.</summary>
    NonCancelableAfterCommit,
}

/// <summary>
/// One strongly typed, self-contained write instruction. Immutable; carries the full target identity.
/// <see cref="Partition"/> is null only for <see cref="WriteOperationKind.CreatePartition"/>.
/// <see cref="SizeBytes"/> is the new size (shrink/extend), the first part's size (split) or the new partition's size (create).
/// </summary>
public sealed record WriteOperationRequest(
    WriteOperationKind Kind,
    DiskIdentity Disk,
    PartitionTarget? Partition,
    string? NewLabel,
    char? NewDriveLetter,
    char? ExpectedCurrentDriveLetter,
    long? OffsetBytes = null,
    long? SizeBytes = null,
    string? FileSystem = null,
    int? ClusterSize = null,
    bool QuickFormat = true,
    PartitionStyle? NewStyle = null,
    PartitionTarget? SecondPartition = null)
{
    public bool IsStructural => Kind >= WriteOperationKind.CreatePartition;

    public static CancellationSemantics Cancellation(WriteOperationKind kind) =>
        kind >= WriteOperationKind.CreatePartition ? CancellationSemantics.NonCancelableAfterCommit : CancellationSemantics.BeforeExecutionOnly;
}

public sealed record ValidationResult(bool IsValid, string Code, string Message)
{
    public static ValidationResult Ok { get; } = new(true, "OK", "");
    public static ValidationResult Fail(string code, string message) => new(false, code, message);
}

/// <summary>Static validation of a request, independent of current disk state. Run by both the UI and the helper.</summary>
public static class WriteRequestValidator
{
    public static ValidationResult Validate(WriteOperationRequest? r)
    {
        if (r is null) return ValidationResult.Fail("MISSING_OPERATION", "The request has no operation.");
        if (!Enum.IsDefined(r.Kind)) return ValidationResult.Fail("NOT_ALLOWED", $"Operation {(int)r.Kind} is not on the allowlist.");
        if (r.Disk is null) return ValidationResult.Fail("MISSING_TARGET", "The request does not identify a disk.");
        if (r.Disk.Number < 0 || r.Disk.Number > 4096) return ValidationResult.Fail("BAD_DISK_NUMBER", "Disk number out of range.");
        if (!r.Disk.HasStableId) return ValidationResult.Fail("WEAK_IDENTITY", "The target disk has no unique ID or serial number.");
        if (string.IsNullOrEmpty(r.Disk.LayoutFingerprint) || r.Disk.LayoutFingerprint.Length != 64 || !r.Disk.LayoutFingerprint.All(Uri.IsHexDigit))
            return ValidationResult.Fail("BAD_FINGERPRINT", "The layout fingerprint is malformed.");
        if (r.Disk.SizeBytes <= 0) return ValidationResult.Fail("BAD_SIZE", "Disk size must be positive.");

        if (r.Kind is WriteOperationKind.CreatePartition or WriteOperationKind.InitializeDisk)
        {
            if (r.Partition is not null) return ValidationResult.Fail("UNEXPECTED_FIELD", $"{r.Kind} must not name an existing partition.");
        }
        else
        {
            if (r.Partition is null) return ValidationResult.Fail("MISSING_TARGET", "The request does not identify a partition.");
            if (r.Partition.PartitionNumber <= 0 || r.Partition.OffsetBytes < 0 || r.Partition.SizeBytes <= 0
                || r.Partition.SizeBytes > r.Disk.SizeBytes || r.Partition.OffsetBytes > r.Disk.SizeBytes - r.Partition.SizeBytes)
                return ValidationResult.Fail("BAD_PARTITION", "The partition geometry is invalid.");
        }

        static bool ValidLetter(char? c) => c is >= 'C' and <= 'Z';
        static bool LabelOk(string? l) => l is null || (l.Length <= 32 && !l.Any(char.IsControl));
        var structuralFields = r.OffsetBytes is not null || r.SizeBytes is not null || r.FileSystem is not null || r.ClusterSize is not null;
        if (r.NewStyle is not null && r.Kind != WriteOperationKind.InitializeDisk) return ValidationResult.Fail("UNEXPECTED_FIELD", "Only Initialize takes a partition style.");
        if (r.SecondPartition is not null && r.Kind != WriteOperationKind.MergePartitions) return ValidationResult.Fail("UNEXPECTED_FIELD", "Only Merge takes a second partition.");

        switch (r.Kind)
        {
            case WriteOperationKind.SetVolumeLabel:
                if (r.NewLabel is null) return ValidationResult.Fail("MISSING_LABEL", "No label given.");
                if (!LabelOk(r.NewLabel)) return ValidationResult.Fail("BAD_LABEL", "The label is too long or contains control characters.");
                if (r.NewDriveLetter is not null || r.ExpectedCurrentDriveLetter is not null) return ValidationResult.Fail("UNEXPECTED_FIELD", "A label change must not carry drive letters.");
                break;
            case WriteOperationKind.AssignDriveLetter:
                if (!ValidLetter(r.NewDriveLetter)) return ValidationResult.Fail("BAD_LETTER", "Drive letter must be C through Z.");
                if (r.ExpectedCurrentDriveLetter is not null) return ValidationResult.Fail("UNEXPECTED_FIELD", "Assigning a letter requires that the partition has none.");
                break;
            case WriteOperationKind.RemoveDriveLetter:
                if (!ValidLetter(r.ExpectedCurrentDriveLetter)) return ValidationResult.Fail("BAD_LETTER", "The current drive letter must be given.");
                if (r.NewDriveLetter is not null) return ValidationResult.Fail("UNEXPECTED_FIELD", "Removing a letter must not name a new one.");
                break;
            case WriteOperationKind.ChangeDriveLetter:
                if (!ValidLetter(r.NewDriveLetter) || !ValidLetter(r.ExpectedCurrentDriveLetter)) return ValidationResult.Fail("BAD_LETTER", "Both drive letters must be C through Z.");
                if (r.NewDriveLetter == r.ExpectedCurrentDriveLetter) return ValidationResult.Fail("NO_CHANGE", "The new letter is the current letter.");
                break;

            case WriteOperationKind.CreatePartition:
                if (r.OffsetBytes is not { } off || r.SizeBytes is not { } size) return ValidationResult.Fail("MISSING_GEOMETRY", "Create needs an offset and a size.");
                if (off < DiskLayout.Alignment || off % DiskLayout.Alignment != 0) return ValidationResult.Fail("BAD_OFFSET", "The new partition must start on a 1 MB boundary after the first megabyte.");
                if (size < DiskLayout.Alignment || size % 512 != 0 || size > r.Disk.SizeBytes || off > r.Disk.SizeBytes - size) return ValidationResult.Fail("BAD_SIZE", "The new partition's size is invalid or does not fit the disk.");
                if (!FileSystemRules.IsFormattable(r.FileSystem ?? "")) return ValidationResult.Fail("BAD_FILE_SYSTEM", "Choose NTFS, FAT32 or exFAT.");
                if (!LabelOk(r.NewLabel)) return ValidationResult.Fail("BAD_LABEL", "The label is too long or contains control characters.");
                if (r.NewDriveLetter is not null && !ValidLetter(r.NewDriveLetter)) return ValidationResult.Fail("BAD_LETTER", "Drive letter must be C through Z.");
                if (r.ExpectedCurrentDriveLetter is not null || r.ClusterSize is not null) return ValidationResult.Fail("UNEXPECTED_FIELD", "Unexpected field for Create.");
                return ValidationResult.Ok;

            case WriteOperationKind.FormatVolume:
                if (!FileSystemRules.IsFormattable(r.FileSystem ?? "")) return ValidationResult.Fail("BAD_FILE_SYSTEM", "Choose NTFS, FAT32 or exFAT.");
                if (r.ClusterSize is { } cs && !FileSystemRules.ClusterSizes(r.FileSystem!).Contains(cs)) return ValidationResult.Fail("BAD_CLUSTER", $"Cluster size {cs} is not valid for {r.FileSystem}.");
                if (!LabelOk(r.NewLabel)) return ValidationResult.Fail("BAD_LABEL", "The label is too long or contains control characters.");
                if (r.OffsetBytes is not null || r.SizeBytes is not null || r.NewDriveLetter is not null || r.ExpectedCurrentDriveLetter is not null)
                    return ValidationResult.Fail("UNEXPECTED_FIELD", "Unexpected field for Format.");
                return ValidationResult.Ok;

            case WriteOperationKind.DeletePartition:
                if (structuralFields || r.NewLabel is not null || r.NewDriveLetter is not null || r.ExpectedCurrentDriveLetter is not null)
                    return ValidationResult.Fail("UNEXPECTED_FIELD", "Delete takes no parameters.");
                return ValidationResult.Ok;

            case WriteOperationKind.ShrinkPartition:
            case WriteOperationKind.ExtendPartition:
                if (r.SizeBytes is not { } newSize || newSize <= 0) return ValidationResult.Fail("MISSING_SIZE", "A new size is required.");
                if (newSize % DiskLayout.Alignment != 0) return ValidationResult.Fail("BAD_SIZE", "The new size must be a whole number of megabytes.");
                if (r.Kind == WriteOperationKind.ShrinkPartition ? newSize >= r.Partition!.SizeBytes : newSize <= r.Partition!.SizeBytes)
                    return ValidationResult.Fail("BAD_SIZE", r.Kind == WriteOperationKind.ShrinkPartition ? "Shrink must make the partition smaller." : "Extend must make the partition larger.");
                if (r.Partition.OffsetBytes > r.Disk.SizeBytes - newSize) return ValidationResult.Fail("BAD_SIZE", "The new size runs past the end of the disk.");
                if (r.OffsetBytes is not null || r.FileSystem is not null || r.ClusterSize is not null || r.NewLabel is not null || r.NewDriveLetter is not null || r.ExpectedCurrentDriveLetter is not null)
                    return ValidationResult.Fail("UNEXPECTED_FIELD", "Resize takes only a new size.");
                return ValidationResult.Ok;

            case WriteOperationKind.InitializeDisk:
                if (r.NewStyle is not (PartitionStyle.Gpt or PartitionStyle.Mbr)) return ValidationResult.Fail("BAD_STYLE", "Choose GPT or MBR.");
                if (structuralFields || r.NewLabel is not null || r.NewDriveLetter is not null || r.ExpectedCurrentDriveLetter is not null)
                    return ValidationResult.Fail("UNEXPECTED_FIELD", "Initialize takes only a partition style.");
                return ValidationResult.Ok;

            case WriteOperationKind.MovePartition:
                if (r.OffsetBytes is not { } dest || dest < DiskLayout.Alignment || dest % DiskLayout.Alignment != 0)
                    return ValidationResult.Fail("BAD_OFFSET", "The destination must start on a 1 MB boundary after the first megabyte.");
                if (dest > r.Disk.SizeBytes - r.Partition!.SizeBytes) return ValidationResult.Fail("BAD_OFFSET", "The destination runs past the end of the disk.");
                if (dest == r.Partition.OffsetBytes) return ValidationResult.Fail("BAD_OFFSET", "The destination equals the current position.");
                // Overlapping destinations are allowed: the block mover copies in an order that never overwrites
                // not-yet-copied data (see RelocationPlan). Free-space checks are done against the live layout.
                if (r.SizeBytes is not null || r.FileSystem is not null || r.ClusterSize is not null || r.NewLabel is not null || r.NewDriveLetter is not null || r.ExpectedCurrentDriveLetter is not null)
                    return ValidationResult.Fail("UNEXPECTED_FIELD", "Move takes only a destination offset.");
                return ValidationResult.Ok;

            case WriteOperationKind.MergePartitions:
                if (r.SecondPartition is not { } src) return ValidationResult.Fail("MISSING_TARGET", "Merge needs the partition to merge in.");
                if (src.PartitionNumber <= 0 || src.SizeBytes <= 0 || src.OffsetBytes < 0 || src.OffsetBytes > r.Disk.SizeBytes - src.SizeBytes)
                    return ValidationResult.Fail("BAD_PARTITION", "The second partition's geometry is invalid.");
                if (src.PartitionNumber == r.Partition!.PartitionNumber || src.OffsetBytes == r.Partition.OffsetBytes)
                    return ValidationResult.Fail("SAME_PARTITION", "A partition cannot be merged into itself.");
                if (structuralFields || r.NewLabel is not null || r.NewDriveLetter is not null || r.ExpectedCurrentDriveLetter is not null)
                    return ValidationResult.Fail("UNEXPECTED_FIELD", "Merge takes only the two partitions.");
                return ValidationResult.Ok;

            case WriteOperationKind.SplitPartition:
                if (r.SizeBytes is not { } first || first <= 0 || first % DiskLayout.Alignment != 0 || first >= r.Partition!.SizeBytes - DiskLayout.Alignment)
                    return ValidationResult.Fail("BAD_SIZE", "The first part must be a whole number of megabytes and leave at least 1 MB for the new partition.");
                if (!FileSystemRules.IsFormattable(r.FileSystem ?? "")) return ValidationResult.Fail("BAD_FILE_SYSTEM", "Choose NTFS, FAT32 or exFAT for the new partition.");
                if (!LabelOk(r.NewLabel)) return ValidationResult.Fail("BAD_LABEL", "The label is too long or contains control characters.");
                if (r.NewDriveLetter is not null && !ValidLetter(r.NewDriveLetter)) return ValidationResult.Fail("BAD_LETTER", "Drive letter must be C through Z.");
                if (r.OffsetBytes is not null || r.ClusterSize is not null || r.ExpectedCurrentDriveLetter is not null) return ValidationResult.Fail("UNEXPECTED_FIELD", "Unexpected field for Split.");
                return ValidationResult.Ok;
        }

        if (structuralFields) return ValidationResult.Fail("UNEXPECTED_FIELD", "Metadata changes must not carry size or file-system fields.");
        if (r.Kind != WriteOperationKind.SetVolumeLabel && r.NewLabel is not null)
            return ValidationResult.Fail("UNEXPECTED_FIELD", "A drive-letter change must not carry a label.");
        return ValidationResult.Ok;
    }

    /// <summary>Label rules that depend on the file system, checked against freshly enumerated state.</summary>
    public static ValidationResult ValidateLabelFor(string fileSystem, string label) =>
        FileSystemRules.ValidateLabel(fileSystem, label) is { } e ? ValidationResult.Fail("BAD_LABEL", e) : ValidationResult.Ok;
}

/// <summary>Turns queued UI operations into helper requests, when (and only when) they map onto the allowlist.</summary>
public static class WriteRequestFactory
{
    public static WriteOperationRequest? TryCreate(DiskOperation op, PreviewState before)
    {
        DiskInfo disk;
        try { disk = before.Disk(op.DiskNumber); } catch (OperationValidationException) { return null; }
        var identity = DiskIdentity.From(disk);
        switch (op)
        {
            case ChangeLabelOperation l when disk.FindPartition(l.PartitionNumber) is { } p:
                return new(WriteOperationKind.SetVolumeLabel, identity, PartitionTarget.From(p), l.NewLabel, null, null);
            case ChangeDriveLetterOperation c when disk.FindPartition(c.PartitionNumber) is { } p:
                return (p.DriveLetter, c.NewLetter) switch
                {
                    (null, { } n) => new(WriteOperationKind.AssignDriveLetter, identity, PartitionTarget.From(p), null, char.ToUpperInvariant(n), null),
                    ({ } cur, null) => new(WriteOperationKind.RemoveDriveLetter, identity, PartitionTarget.From(p), null, null, cur),
                    ({ } cur, { } n) => new(WriteOperationKind.ChangeDriveLetter, identity, PartitionTarget.From(p), null, char.ToUpperInvariant(n), cur),
                    _ => null,
                };
            case CreatePartitionOperation c:
                return new(WriteOperationKind.CreatePartition, identity, null, c.Label, c.DriveLetter is { } dl ? char.ToUpperInvariant(dl) : null, null,
                    OffsetBytes: c.OffsetBytes, SizeBytes: c.SizeBytes, FileSystem: FileSystemRules.Normalize(c.FileSystem));
            case FormatPartitionOperation f when disk.FindPartition(f.PartitionNumber) is { } p:
                return new(WriteOperationKind.FormatVolume, identity, PartitionTarget.From(p), f.Label, null, null,
                    FileSystem: FileSystemRules.Normalize(f.FileSystem), ClusterSize: f.ClusterSize == 0 ? null : f.ClusterSize, QuickFormat: f.QuickFormat);
            case DeletePartitionOperation del when disk.FindPartition(del.PartitionNumber) is { } p:
                return new(WriteOperationKind.DeletePartition, identity, PartitionTarget.From(p), null, null, null);
            case ResizeMovePartitionOperation r when disk.FindPartition(r.PartitionNumber) is { } p && r.NewOffsetBytes == p.OffsetBytes && r.NewSizeBytes != p.SizeBytes:
                return new(r.NewSizeBytes < p.SizeBytes ? WriteOperationKind.ShrinkPartition : WriteOperationKind.ExtendPartition,
                    identity, PartitionTarget.From(p), null, null, null, SizeBytes: r.NewSizeBytes);
            case SplitPartitionOperation s when disk.FindPartition(s.PartitionNumber) is { } p:
                return new(WriteOperationKind.SplitPartition, identity, PartitionTarget.From(p), s.NewLabel, s.NewDriveLetter is { } nl ? char.ToUpperInvariant(nl) : null, null,
                    SizeBytes: s.FirstPartSizeBytes, FileSystem: FileSystemRules.Normalize(s.NewFileSystem));
            case InitializeDiskOperation i:
                return new(WriteOperationKind.InitializeDisk, identity, null, null, null, null, NewStyle: i.Style);
            case ResizeMovePartitionOperation m when disk.FindPartition(m.PartitionNumber) is { } mp && m.NewOffsetBytes != mp.OffsetBytes && m.NewSizeBytes == mp.SizeBytes:
                return new(WriteOperationKind.MovePartition, identity, PartitionTarget.From(mp), null, null, null, OffsetBytes: m.NewOffsetBytes);
            case MergePartitionsOperation g when disk.FindPartition(g.TargetPartitionNumber) is { } gt && disk.FindPartition(g.SourcePartitionNumber) is { } gs:
                return new(WriteOperationKind.MergePartitions, identity, PartitionTarget.From(gt), null, null, null, SecondPartition: PartitionTarget.From(gs));
            default:
                return null; // move-and-resize at once, delete-all, hide/show: not available as real operations
        }
    }
}
