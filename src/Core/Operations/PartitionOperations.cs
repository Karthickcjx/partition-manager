using PartitionManager.Core.FileSystems;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Models;

namespace PartitionManager.Core.Operations;

public sealed record CreatePartitionOperation(int DiskNumber, long OffsetBytes, long SizeBytes, string FileSystem, string Label, char? DriveLetter)
    : DiskOperation(DiskNumber)
{
    public override string Title => "Create partition";
    public override RiskLevel Risk => RiskLevel.Medium;
    public override IReadOnlyList<string> Steps => ["Creating partition...", "Formatting partition...", "Assigning drive letter..."];

    public override string Describe(PreviewState before) =>
        $"Create {ByteSize.Format(SizeBytes)} {FileSystemRules.Normalize(FileSystem)} partition" +
        (DriveLetter is { } c ? $" ({char.ToUpperInvariant(c)}:)" : "") + $" on Disk {DiskNumber}";

    public override PreviewState Apply(PreviewState state)
    {
        var disk = state.Disk(DiskNumber);
        Check(disk.Style is PartitionStyle.Mbr or PartitionStyle.Gpt, $"{disk.Title} is not initialized. Initialize it as MBR or GPT first.");
        CheckAligned(disk, OffsetBytes, SizeBytes);
        var end = OffsetBytes + SizeBytes;
        Check(DiskLayout.FreeSegments(disk).Any(s => s.OffsetBytes <= OffsetBytes && end <= s.EndBytes),
            "The new partition must lie entirely inside unallocated space.");
        if (FileSystemRules.ValidateSizeForFormat(FileSystem, SizeBytes) is { } sizeError) throw new OperationValidationException(sizeError);
        if (FileSystemRules.ValidateLabel(FileSystem, Label) is { } labelError) throw new OperationValidationException(labelError);
        CheckLetterFree(state, DriveLetter);
        CheckMbrSlot(disk, 1);

        var created = NewBasicPartition(disk, disk.NextPartitionNumber(), OffsetBytes, SizeBytes, FileSystem, Label, DriveLetter);
        var next = disk.WithPartitions(disk.Partitions.Append(created));
        CheckFitsLayout(next);
        return state.With(next);
    }
}

public sealed record DeletePartitionOperation(int DiskNumber, int PartitionNumber) : DiskOperation(DiskNumber)
{
    public override string Title => "Delete partition";
    public override RiskLevel Risk => RiskLevel.Destructive;
    public override IReadOnlyList<string> Steps => ["Deleting partition..."];
    public override IReadOnlyList<int> AffectedPartitions => [PartitionNumber];

    public override string Describe(PreviewState before) => $"Delete {Name(before.Disk(DiskNumber), PartitionNumber)}";

    public override PreviewState Apply(PreviewState state)
    {
        var disk = state.Disk(DiskNumber);
        var p = Require(disk, PartitionNumber);
        if (p.IsExtendedContainer)
            Check(!disk.Partitions.Any(l => l.IsLogical), "Delete the logical partitions inside the extended partition first.");
        return state.With(disk.WithPartitions(disk.Partitions.Where(x => x.PartitionNumber != PartitionNumber)));
    }
}

public sealed record DeleteAllPartitionsOperation(int DiskNumber) : DiskOperation(DiskNumber)
{
    public override string Title => "Delete all partitions";
    public override RiskLevel Risk => RiskLevel.Destructive;
    public override IReadOnlyList<string> Steps => ["Deleting all partitions..."];

    public override string Describe(PreviewState before)
    {
        var disk = before.Disk(DiskNumber);
        return $"Delete all {disk.Partitions.Count} partitions on {disk.Title} ({disk.Model})";
    }

    public override PreviewState Apply(PreviewState state)
    {
        var disk = state.Disk(DiskNumber);
        Check(disk.Partitions.Count > 0, $"{disk.Title} has no partitions.");
        return state.With(disk.WithPartitions([]));
    }
}

public sealed record FormatPartitionOperation(int DiskNumber, int PartitionNumber, string FileSystem, string Label, int ClusterSize, bool QuickFormat)
    : DiskOperation(DiskNumber)
{
    public override string Title => "Format partition";
    public override RiskLevel Risk => RiskLevel.Destructive;
    public override IReadOnlyList<string> Steps => QuickFormat ? ["Formatting partition..."] : ["Checking sectors...", "Formatting partition..."];
    public override IReadOnlyList<int> AffectedPartitions => [PartitionNumber];

    public override string Describe(PreviewState before) =>
        $"Format {Name(before.Disk(DiskNumber), PartitionNumber)} as {FileSystemRules.Normalize(FileSystem)}" + (QuickFormat ? "" : " (full format)");

    public override PreviewState Apply(PreviewState state)
    {
        var disk = state.Disk(DiskNumber);
        var p = Require(disk, PartitionNumber);
        Check(!p.IsExtendedContainer, "An extended partition cannot be formatted.");
        if (FileSystemRules.ValidateSizeForFormat(FileSystem, p.SizeBytes) is { } e1) throw new OperationValidationException(e1);
        if (FileSystemRules.ValidateLabel(FileSystem, Label) is { } e2) throw new OperationValidationException(e2);
        Check(FileSystemRules.ClusterSizes(FileSystem).Contains(ClusterSize), $"Cluster size {ClusterSize} is not valid for {FileSystem}.");

        var formatted = p with
        {
            FileSystem = FileSystemRules.Normalize(FileSystem),
            Label = Label,
            VolumeSizeBytes = p.SizeBytes,
            FreeBytes = p.SizeBytes,
            BitLocker = BitLockerState.NotEncrypted,
            Role = p.Role is PartitionRole.Unknown or PartitionRole.Other ? PartitionRole.BasicData : p.Role,
            MbrType = disk.Style == PartitionStyle.Mbr ? FileSystemRules.MbrTypeFor(FileSystem) : p.MbrType,
        };
        return state.With(disk.WithPartitions(disk.Partitions.Select(x => x.PartitionNumber == PartitionNumber ? formatted : x)));
    }
}

public sealed record ResizeMovePartitionOperation(int DiskNumber, int PartitionNumber, long NewOffsetBytes, long NewSizeBytes)
    : DiskOperation(DiskNumber)
{
    public override string Title => "Resize/Move partition";

    // Risk depends on the original geometry, which the record does not carry; moving or shrinking is the worst case.
    public override RiskLevel Risk => RiskLevel.High;
    public override IReadOnlyList<int> AffectedPartitions => [PartitionNumber];

    public override IReadOnlyList<string> Steps => ["Resizing partition..."];

    public override IReadOnlyList<string> GetSteps(PreviewState before)
    {
        if (before.Disk(DiskNumber).FindPartition(PartitionNumber) is not { } p) return Steps;
        var steps = new List<string>();
        if (NewSizeBytes < p.SizeBytes) steps.Add("Shrinking partition...");
        if (NewOffsetBytes != p.OffsetBytes) { steps.Add("Moving partition..."); steps.Add("Moving file data..."); }
        if (NewSizeBytes > p.SizeBytes) steps.Add("Extending partition...");
        return steps.Count == 0 ? Steps : steps;
    }

    public override string Describe(PreviewState before)
    {
        var disk = before.Disk(DiskNumber);
        var p = disk.FindPartition(PartitionNumber);
        if (p is null) return $"Resize partition {PartitionNumber} on {disk.Title}";
        var verb = p.OffsetBytes != NewOffsetBytes ? (p.SizeBytes != NewSizeBytes ? "Move and resize" : "Move") : (NewSizeBytes > p.SizeBytes ? "Extend" : "Shrink");
        return $"{verb} {p.DisplayName} on {disk.Title} from {ByteSize.Format(p.SizeBytes)} to {ByteSize.Format(NewSizeBytes)}";
    }

    public override PreviewState Apply(PreviewState state)
    {
        var disk = state.Disk(DiskNumber);
        var p = Require(disk, PartitionNumber);
        Check(!p.IsExtendedContainer, "Resizing an extended container is not supported.");
        Check(p.Role is not (PartitionRole.EfiSystem or PartitionRole.MicrosoftReserved), p.Role == PartitionRole.EfiSystem
            ? "Couldn't resize boot partition!" : "Couldn't resize MSR partition!");
        Check(NewOffsetBytes != p.OffsetBytes || NewSizeBytes != p.SizeBytes, "The new size and position are the same as the current ones.");
        CheckAligned(disk, NewOffsetBytes, NewSizeBytes);

        var (min, max) = DiskLayout.MovableRange(disk, p);
        Check(NewOffsetBytes >= min && NewOffsetBytes + NewSizeBytes <= max, "Not enough space! The partition cannot overlap its neighbours.");

        var moving = NewOffsetBytes != p.OffsetBytes;
        var shrinking = NewSizeBytes < p.SizeBytes;
        var growing = NewSizeBytes > p.SizeBytes;
        if (shrinking) Check(FileSystemRules.CanShrink(p.FileSystem), $"{p.FileSystem} partitions cannot be shrunk.");
        if (growing) Check(FileSystemRules.CanExtend(p.FileSystem), $"{p.FileSystem} partitions cannot be extended.");
        if (moving) Check(FileSystemRules.CanRelocateData(p.FileSystem), $"{p.FileSystem} partitions cannot be moved.");
        if (shrinking && p.UsedBytes is { } used)
            Check(NewSizeBytes >= used + FileSystemRules.ShrinkHeadroomBytes,
                $"Not enough space! {ByteSize.Format(used)} is in use, so the partition must stay at least {ByteSize.Format(used + FileSystemRules.ShrinkHeadroomBytes)}.");

        var resized = p with
        {
            OffsetBytes = NewOffsetBytes,
            SizeBytes = NewSizeBytes,
            VolumeSizeBytes = p.HasFileSystem ? NewSizeBytes : p.VolumeSizeBytes,
            FreeBytes = p.UsedBytes is { } u ? NewSizeBytes - u : p.FreeBytes,
        };
        var next = disk.WithPartitions(disk.Partitions.Select(x => x.PartitionNumber == PartitionNumber ? resized : x));
        CheckFitsLayout(next);
        return state.With(next);
    }
}

public sealed record SplitPartitionOperation(int DiskNumber, int PartitionNumber, long FirstPartSizeBytes, string NewFileSystem, string NewLabel, char? NewDriveLetter)
    : DiskOperation(DiskNumber)
{
    public override string Title => "Split partition";
    public override RiskLevel Risk => RiskLevel.High;
    public override IReadOnlyList<string> Steps => ["Shrinking partition...", "Creating new partition...", "Formating new partition..."];
    public override IReadOnlyList<int> AffectedPartitions => [PartitionNumber];

    public override string Describe(PreviewState before) =>
        $"Split {Name(before.Disk(DiskNumber), PartitionNumber)}, keeping {ByteSize.Format(FirstPartSizeBytes)} in the first part";

    public override PreviewState Apply(PreviewState state)
    {
        var disk = state.Disk(DiskNumber);
        var p = Require(disk, PartitionNumber);
        Check(!p.IsExtendedContainer && !p.IsProtectedRole, "This partition cannot be split.");
        Check(FileSystemRules.CanShrink(p.FileSystem), $"{p.FileSystem} partitions cannot be split.");
        Check(FirstPartSizeBytes % DiskLayout.Alignment == 0, "The split point must be aligned to 1 MB.");
        var secondOffset = p.OffsetBytes + FirstPartSizeBytes;
        var secondSize = p.EndBytes - secondOffset;
        Check(FirstPartSizeBytes > 0 && secondSize >= DiskLayout.Alignment, "Both parts must be at least 1 MB.");
        if (p.UsedBytes is { } used)
            Check(FirstPartSizeBytes >= used + FileSystemRules.ShrinkHeadroomBytes,
                $"Not enough space! The first part must hold the {ByteSize.Format(used)} already in use.");
        if (FileSystemRules.ValidateSizeForFormat(NewFileSystem, secondSize) is { } e1) throw new OperationValidationException(e1);
        if (FileSystemRules.ValidateLabel(NewFileSystem, NewLabel) is { } e2) throw new OperationValidationException(e2);
        CheckLetterFree(state, NewDriveLetter);
        if (!p.IsLogical) CheckMbrSlot(disk, 1);

        var first = p with
        {
            SizeBytes = FirstPartSizeBytes,
            VolumeSizeBytes = FirstPartSizeBytes,
            FreeBytes = p.UsedBytes is { } u ? FirstPartSizeBytes - u : p.FreeBytes,
        };
        var second = NewBasicPartition(disk, disk.NextPartitionNumber(), secondOffset, secondSize, NewFileSystem, NewLabel, NewDriveLetter)
            with { IsLogical = p.IsLogical };
        var next = disk.WithPartitions(disk.Partitions.Select(x => x.PartitionNumber == PartitionNumber ? first : x).Append(second));
        CheckFitsLayout(next);
        return state.With(next);
    }
}

/// <summary>Merges <see cref="SourcePartitionNumber"/> into <see cref="TargetPartitionNumber"/>; the source's files move into a folder on the target.</summary>
public sealed record MergePartitionsOperation(int DiskNumber, int TargetPartitionNumber, int SourcePartitionNumber) : DiskOperation(DiskNumber)
{
    public override string Title => "Merge partitions";
    public override RiskLevel Risk => RiskLevel.High;
    public override IReadOnlyList<string> Steps => ["Fetching file information...", "Merging data...", "Deleting partition...", "Extending partition..."];
    public override IReadOnlyList<int> AffectedPartitions => [TargetPartitionNumber, SourcePartitionNumber];

    public override string Describe(PreviewState before)
    {
        var disk = before.Disk(DiskNumber);
        return $"Merge {Name(disk, SourcePartitionNumber)} into {disk.FindPartition(TargetPartitionNumber)?.DisplayName ?? "partition " + TargetPartitionNumber}";
    }

    public override PreviewState Apply(PreviewState state)
    {
        var disk = state.Disk(DiskNumber);
        var target = Require(disk, TargetPartitionNumber);
        var source = Require(disk, SourcePartitionNumber);
        Check(TargetPartitionNumber != SourcePartitionNumber, "Choose two different partitions.");
        Check(!target.IsProtectedRole && !source.IsProtectedRole && !target.IsExtendedContainer && !source.IsExtendedContainer,
            "System-reserved partitions cannot be merged.");
        Check(FileSystemRules.CanExtend(target.FileSystem), $"The target partition ({target.FileSystem}) cannot be extended.");
        Check(FileSystemRules.CanRelocateData(source.FileSystem), $"Files on a {source.FileSystem} partition cannot be merged.");

        var start = Math.Min(target.OffsetBytes, source.OffsetBytes);
        var end = Math.Max(target.EndBytes, source.EndBytes);
        var between = DiskLayout.MapPartitions(disk).Where(x =>
            x.PartitionNumber != TargetPartitionNumber && x.PartitionNumber != SourcePartitionNumber && x.OffsetBytes < end && x.EndBytes > start);
        Check(!between.Any(), "Only adjacent partitions can be merged.");

        var used = (target.UsedBytes ?? 0) + (source.UsedBytes ?? 0);
        var size = end - start;
        Check(used + FileSystemRules.ShrinkHeadroomBytes <= size, "Not enough space to hold the files of both partitions.");

        var merged = target with { OffsetBytes = start, SizeBytes = size, VolumeSizeBytes = size, FreeBytes = size - used };
        var next = disk.WithPartitions(disk.Partitions
            .Where(x => x.PartitionNumber != SourcePartitionNumber)
            .Select(x => x.PartitionNumber == TargetPartitionNumber ? merged : x));
        CheckFitsLayout(next);
        return state.With(next);
    }
}

public sealed record ChangeDriveLetterOperation(int DiskNumber, int PartitionNumber, char? NewLetter) : DiskOperation(DiskNumber)
{
    public override string Title => "Change drive letter";
    public override RiskLevel Risk => RiskLevel.Low;
    public override IReadOnlyList<string> Steps => ["Changing drive letter..."];
    public override IReadOnlyList<int> AffectedPartitions => [PartitionNumber];

    public override string Describe(PreviewState before) =>
        $"Change drive letter of {Name(before.Disk(DiskNumber), PartitionNumber)} to {(NewLetter is { } c ? char.ToUpperInvariant(c) + ":" : "none")}";

    public override PreviewState Apply(PreviewState state)
    {
        var disk = state.Disk(DiskNumber);
        var p = Require(disk, PartitionNumber);
        Check(p.HasFileSystem, "Only formatted partitions can have a drive letter.");
        Check(p.DriveLetter != NewLetter, "You need to choose one drive leter different from the current one.");
        CheckLetterFree(state, NewLetter, p.DriveLetter);
        var updated = p with { DriveLetter = NewLetter is { } c ? char.ToUpperInvariant(c) : null };
        return state.With(disk.WithPartitions(disk.Partitions.Select(x => x.PartitionNumber == PartitionNumber ? updated : x)));
    }
}

public sealed record ChangeLabelOperation(int DiskNumber, int PartitionNumber, string NewLabel) : DiskOperation(DiskNumber)
{
    public override string Title => "Change volume label";
    public override RiskLevel Risk => RiskLevel.Low;
    public override IReadOnlyList<string> Steps => ["Changing volume label..."];
    public override IReadOnlyList<int> AffectedPartitions => [PartitionNumber];

    public override string Describe(PreviewState before) =>
        $"Change label of {Name(before.Disk(DiskNumber), PartitionNumber)} to \"{NewLabel}\"";

    public override PreviewState Apply(PreviewState state)
    {
        var disk = state.Disk(DiskNumber);
        var p = Require(disk, PartitionNumber);
        Check(p.HasFileSystem, "Only formatted partitions have a volume label.");
        if (FileSystemRules.ValidateLabel(p.FileSystem, NewLabel) is { } e) throw new OperationValidationException(e);
        var updated = p with { Label = NewLabel };
        return state.With(disk.WithPartitions(disk.Partitions.Select(x => x.PartitionNumber == PartitionNumber ? updated : x)));
    }
}

public sealed record SetPartitionHiddenOperation(int DiskNumber, int PartitionNumber, bool Hidden, char? LetterWhenShown = null) : DiskOperation(DiskNumber)
{
    public override string Title => Hidden ? "Hide partition" : "Show partition";
    public override RiskLevel Risk => RiskLevel.Low;
    public override IReadOnlyList<string> Steps => [Hidden ? "Hiding partition..." : "Showing partition..."];
    public override IReadOnlyList<int> AffectedPartitions => [PartitionNumber];

    public override string Describe(PreviewState before) => $"{(Hidden ? "Hide" : "Show")} {Name(before.Disk(DiskNumber), PartitionNumber)}";

    public override PreviewState Apply(PreviewState state)
    {
        var disk = state.Disk(DiskNumber);
        var p = Require(disk, PartitionNumber);
        Check(p.IsHidden != Hidden, Hidden ? "The partition is already hidden." : "The partition is not hidden.");
        if (!Hidden) CheckLetterFree(state, LetterWhenShown);
        var updated = Hidden
            ? p with { IsHidden = true, DriveLetter = null }
            : p with { IsHidden = false, DriveLetter = LetterWhenShown is { } c ? char.ToUpperInvariant(c) : null };
        return state.With(disk.WithPartitions(disk.Partitions.Select(x => x.PartitionNumber == PartitionNumber ? updated : x)));
    }
}

public sealed record InitializeDiskOperation(int DiskNumber, PartitionStyle Style) : DiskOperation(DiskNumber)
{
    public override string Title => "Initialize disk";
    public override RiskLevel Risk => RiskLevel.Destructive;
    public override IReadOnlyList<string> Steps => ["Initializing disk...", "Writing partition table..."];

    public override string Describe(PreviewState before) => $"Initialize Disk {DiskNumber} as {(Style == PartitionStyle.Gpt ? "GPT" : "MBR")}";

    public override PreviewState Apply(PreviewState state)
    {
        var disk = state.Disk(DiskNumber);
        Check(Style is PartitionStyle.Mbr or PartitionStyle.Gpt, "Choose MBR or GPT.");
        Check(disk.Style is PartitionStyle.Raw or PartitionStyle.Unknown && disk.Partitions.Count == 0,
            $"{disk.Title} is already initialized.");
        return state.With(disk with { Style = Style });
    }
}
