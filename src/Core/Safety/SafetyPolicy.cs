using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;

namespace PartitionManager.Core.Safety;

public sealed record SafetyVerdict(IReadOnlyList<string> Blockers, IReadOnlyList<string> Warnings)
{
    public bool IsAllowed => Blockers.Count == 0;
    public static SafetyVerdict Allowed { get; } = new([], []);
}

/// <summary>
/// Policy checks that sit above structural validation: they protect the running system, encrypted volumes and
/// read-only media, and spell out what each operation will put at risk.
/// </summary>
public sealed class SafetyPolicy
{
    /// <summary>When false (the default), destructive changes to the disk that hosts the running Windows are refused outright.</summary>
    public bool AllowSystemDiskDestructiveOperations { get; init; }

    public SafetyVerdict Evaluate(DiskOperation op, PreviewState state)
    {
        var blockers = new List<string>();
        var warnings = new List<string>();
        DiskInfo disk;
        try { disk = state.Disk(op.DiskNumber); }
        catch (OperationValidationException ex) { return new([ex.Message], []); }

        if (disk.IsReadOnly) blockers.Add($"{disk.Title} is read-only.");
        if (disk.IsOffline) blockers.Add($"{disk.Title} is offline. Bring it online first.");
        if (disk.Health == HealthState.Unhealthy && op.Risk >= RiskLevel.High)
            blockers.Add($"{disk.Title} reports an unhealthy status. Back up its data instead of restructuring it.");
        else if (disk.Health == HealthState.Warning && op.Risk >= RiskLevel.Medium)
            warnings.Add($"{disk.Title} reports a health warning.");

        var parts = op.AffectedPartitions.Select(disk.FindPartition).OfType<PartitionInfo>().ToList();

        switch (op)
        {
            case DeleteAllPartitionsOperation or InitializeDiskOperation when disk.HostsRunningSystem:
                blockers.Add($"{disk.Title} holds the running Windows installation; wiping it is not allowed.");
                break;
            case DeleteAllPartitionsOperation when disk.Partitions.Any(p => p.IsEncrypted):
                warnings.Add($"{disk.Title} contains BitLocker-encrypted volumes. Their data and keys will be lost.");
                break;
        }

        foreach (var p in parts)
        {
            var name = p.DisplayName;
            var runningSystem = p.IsBoot || p.IsSystem;

            if (op is DeletePartitionOperation or FormatPartitionOperation)
            {
                if (runningSystem && !AllowSystemDiskDestructiveOperations)
                    blockers.Add($"{name} is in use by the running Windows installation and cannot be deleted or formatted.");
                if (p.IsProtectedRole)
                    blockers.Add($"{name} is a {PartitionTypes.RoleDisplayName(p.Role).ToLowerInvariant()} that Windows needs to boot or recover.");
            }

            if (op is MergePartitionsOperation merge && p.PartitionNumber == merge.SourcePartitionNumber && runningSystem)
                blockers.Add($"{name} hosts Windows and cannot be merged into another partition. Merge the other partition into it instead.");

            if (op is ResizeMovePartitionOperation rm && runningSystem && rm.NewOffsetBytes != p.OffsetBytes)
                blockers.Add($"Moving {name} while Windows is running from it is not supported.");

            if (op is SplitPartitionOperation && runningSystem)
                warnings.Add($"{name} hosts Windows; splitting it shrinks the system volume.");

            if (p.IsEncrypted && op is ResizeMovePartitionOperation or SplitPartitionOperation or MergePartitionsOperation)
                blockers.Add($"The partition is encrypted by BitLocker! Suspend or decrypt {name} before changing its layout.");
            else if (p.BitLocker == BitLockerState.Locked)
                blockers.Add($"{name} is locked by BitLocker. Unlock it first.");
            else if (p.BitLocker == BitLockerState.Unknown && p.HasFileSystem && op.Risk >= RiskLevel.High)
                warnings.Add($"BitLocker status of {name} could not be read (run as administrator to check).");

            if (op is ChangeDriveLetterOperation && runningSystem)
                blockers.Add($"The drive letter of {name} cannot be changed while Windows is running from it.");

            if (op is SetPartitionHiddenOperation { Hidden: true } && runningSystem)
                blockers.Add($"{name} hosts Windows and cannot be hidden.");

            if (op.IsDestructive && p.UsedBytes is > 0)
                warnings.Add($"All data on {name} ({ByteSize.Format(p.UsedBytes)}) will be permanently erased.");
            else if (op.Risk == RiskLevel.High)
                warnings.Add($"{name} will be rewritten. Back up important files first; an interruption can corrupt the volume.");
        }

        return new SafetyVerdict(blockers.Distinct().ToList(), warnings.Distinct().ToList());
    }
}
