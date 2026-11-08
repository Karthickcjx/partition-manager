using PartitionManager.Core.Layout;
using PartitionManager.Core.Models;

namespace PartitionManager.Core.Operations;

public enum RiskLevel
{
    /// <summary>Metadata only (label, drive letter).</summary>
    Low,
    /// <summary>Writes into free space or grows a volume.</summary>
    Medium,
    /// <summary>Relocates or shrinks existing data; a failure mid-way can corrupt the volume.</summary>
    High,
    /// <summary>Erases data by design.</summary>
    Destructive,
}

/// <summary>
/// A pending change to one disk. Operations are pure: <see cref="Apply"/> returns the preview layout and never touches hardware.
/// Executors translate them into real or simulated work.
/// </summary>
public abstract record DiskOperation(int DiskNumber)
{
    public abstract string Title { get; }
    public abstract RiskLevel Risk { get; }
    public bool IsDestructive => Risk == RiskLevel.Destructive;

    /// <summary>Human-readable summary, rendered against the state before the operation is applied.</summary>
    public abstract string Describe(PreviewState before);

    /// <summary>Validates against <paramref name="state"/> and returns the resulting state. Throws <see cref="OperationValidationException"/>.</summary>
    public abstract PreviewState Apply(PreviewState state);

    /// <summary>Progress steps shown while the operation executes.</summary>
    public abstract IReadOnlyList<string> Steps { get; }

    /// <summary>Steps for this operation given the state it will run against (some depend on the current geometry).</summary>
    public virtual IReadOnlyList<string> GetSteps(PreviewState before) => Steps;

    /// <summary>Partition numbers (in the pre-operation state) this operation reads or writes.</summary>
    public virtual IReadOnlyList<int> AffectedPartitions => [];

    protected static PartitionInfo Require(DiskInfo disk, int partitionNumber) =>
        disk.FindPartition(partitionNumber)
        ?? throw new OperationValidationException($"Partition {partitionNumber} was not found on {disk.Title}.");

    protected static string Name(DiskInfo disk, int partitionNumber) =>
        disk.FindPartition(partitionNumber) is { } p ? $"{p.DisplayName} on {disk.Title}" : $"partition {partitionNumber} on {disk.Title}";

    protected static void Check(bool condition, string message)
    {
        if (!condition) throw new OperationValidationException(message);
    }

    protected static void CheckAligned(DiskInfo disk, long offset, long size)
    {
        Check(offset % DiskLayout.Alignment == 0, "The partition start must be aligned to 1 MB.");
        Check(size > 0 && size % disk.LogicalSectorSize == 0, $"The partition size must be a whole number of {disk.LogicalSectorSize}-byte sectors.");
    }

    protected static void CheckLetterFree(PreviewState state, char? letter, char? currentLetter = null)
    {
        if (letter is not { } c) return;
        c = char.ToUpperInvariant(c);
        Check(c is >= 'C' and <= 'Z', $"Drive letter {c}: is not allowed. Choose C through Z.");
        if (currentLetter is { } cur && char.ToUpperInvariant(cur) == c) return;
        Check(!state.UsedLetters.Contains(c), $"The drive letter {c}: is in use.");
    }

    protected static void CheckMbrSlot(DiskInfo disk, int additionalPrimaries)
    {
        if (disk.Style != PartitionStyle.Mbr) return;
        var primaries = disk.Partitions.Count(p => !p.IsLogical);
        Check(primaries + additionalPrimaries <= 4, "MBR disks support at most 4 primary partitions. Convert the disk to GPT first.");
    }

    protected static void CheckFitsLayout(DiskInfo disk)
    {
        var errors = LayoutValidator.Validate(disk).Where(i => i.Severity == IssueSeverity.Error).ToList();
        Check(errors.Count == 0, errors.Count == 0 ? "" : errors[0].Message);
    }

    protected static PartitionInfo NewBasicPartition(DiskInfo disk, int number, long offset, long size, string fs, string label, char? letter) => new()
    {
        DiskNumber = disk.Number,
        PartitionNumber = number,
        OffsetBytes = offset,
        SizeBytes = size,
        Role = PartitionRole.BasicData,
        GptType = disk.Style == PartitionStyle.Gpt ? PartitionTypes.BasicData : null,
        MbrType = disk.Style == PartitionStyle.Mbr ? FileSystems.FileSystemRules.MbrTypeFor(fs) : null,
        FileSystem = FileSystems.FileSystemRules.Normalize(fs),
        Label = label,
        DriveLetter = letter is { } c ? char.ToUpperInvariant(c) : null,
        VolumeSizeBytes = size,
        FreeBytes = size,
        BitLocker = BitLockerState.NotEncrypted,
    };
}
