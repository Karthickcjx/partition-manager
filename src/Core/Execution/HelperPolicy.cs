using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;

namespace PartitionManager.Core.Execution;

/// <summary>
/// What the privileged helper is allowed to write. There is deliberately no mode for physical disks: that requires a
/// separately authorized validation process and a code change, not a configuration switch.
/// </summary>
public enum WriteMode { Disabled, VirtualDisksOnly }

/// <summary>
/// Write policy. Read by the helper from HKLM (administrator-writable only); the UI reads the same values to decide
/// whether to offer real execution, but the helper never trusts the UI's decision.
/// </summary>
public sealed record HelperPolicy(WriteMode Mode, IReadOnlyList<string> AllowedImageRoots)
{
    // Issued by an administrator immediately after creating a disposable blank image.
    // Never accepted from a write request. Absence means initialization is refused.
    public IReadOnlyList<BlankImageApproval> InitializationApprovals { get; init; } = [];

    public bool ApprovesInitialization(DiskInfo disk, DateTimeOffset now) =>
        disk.IsVirtual && disk.Style == PartitionStyle.Raw && disk.Partitions.Count == 0 &&
        AllowsImage(disk.VirtualImagePath) && InitializationApprovals.Count(a =>
            a.CreatedUtc <= now && now - a.CreatedUtc <= TimeSpan.FromMinutes(10) &&
            a.Disk == DiskIdentity.From(disk)) == 1;

    /// <summary>
    /// TEST MACHINES ONLY. MSFT_Disk.UniqueId values of physical disks an administrator explicitly enrolled for real
    /// operations. Never written by the installer; absent on every normal PC. On an enrolled disk the running system,
    /// EFI, MSR and recovery partitions still allow only in-place shrink/extend (see <see cref="AllowsPhysicalTest"/>);
    /// Initialize and Merge stay refused.
    /// </summary>
    public IReadOnlyList<string> PhysicalTestDiskIds { get; init; } = [];

    /// <summary>Operations allowed on any partition of an enrolled disk, including the running system volume.</summary>
    public static readonly WriteOperationKind[] PhysicalSystemKinds = [WriteOperationKind.ShrinkPartition, WriteOperationKind.ExtendPartition];

    /// <summary>Operations allowed on non-system partitions / free space of an enrolled disk.</summary>
    public static readonly WriteOperationKind[] PhysicalTestKinds =
    [
        WriteOperationKind.ShrinkPartition, WriteOperationKind.ExtendPartition, WriteOperationKind.CreatePartition, WriteOperationKind.FormatVolume,
        WriteOperationKind.DeletePartition, WriteOperationKind.SetVolumeLabel, WriteOperationKind.AssignDriveLetter, WriteOperationKind.RemoveDriveLetter,
        WriteOperationKind.ChangeDriveLetter, WriteOperationKind.SplitPartition, WriteOperationKind.MovePartition,
    ];

    public bool IsEnrolledPhysicalTestDisk(DiskInfo disk) =>
        Mode == WriteMode.VirtualDisksOnly && !disk.IsVirtual && !string.IsNullOrWhiteSpace(disk.UniqueId) &&
        PhysicalTestDiskIds.Any(id => string.Equals(id.Trim(), disk.UniqueId.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True for an enrolled physical disk when the operation is allowed for its target: system/boot/EFI/MSR/recovery
    /// partitions only in-place shrink/extend; other partitions and free space the full real set (minus Initialize/Merge).
    /// </summary>
    public bool AllowsPhysicalTest(DiskInfo disk, WriteOperationKind kind, PartitionInfo? target)
    {
        if (!IsEnrolledPhysicalTestDisk(disk) || !PhysicalTestKinds.Contains(kind)) return false;
        var critical = target is not null && (target.IsBoot || target.IsSystem || target.IsProtectedRole || target.IsExtendedContainer);
        return !critical || PhysicalSystemKinds.Contains(kind);
    }

    /// <summary>Back-compat name used by older call sites/tests: shrink/extend on an enrolled disk.</summary>
    public bool AllowsPhysicalResize(DiskInfo disk, WriteOperationKind kind) =>
        PhysicalSystemKinds.Contains(kind) && AllowsPhysicalTest(disk, kind, null);

    public static HelperPolicy Disabled { get; } = new(WriteMode.Disabled, []);

    public const string RegistryKey = @"SOFTWARE\PartitionManager\Helper";

    /// <summary>True when <paramref name="imagePath"/> is a VHD/VHDX file inside one of the allowed local folders.</summary>
    public bool AllowsImage(string? imagePath)
    {
        if (Mode != WriteMode.VirtualDisksOnly || string.IsNullOrWhiteSpace(imagePath)) return false;
        if (!TryNormalizeLocal(imagePath, out var full)) return false;
        if (!(full.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase))) return false;
        foreach (var root in AllowedImageRoots)
        {
            if (!TryNormalizeLocal(root, out var r)) continue;
            r = r.TrimEnd('\\') + "\\";
            if (r.Length <= 3) continue; // a whole drive (C:\) is never an acceptable test folder
            if (full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool TryNormalizeLocal(string path, out string full)
    {
        full = "";
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains('\0')) return false; // UNC, device and NT paths
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\') return false;
        if (path[2..].Contains(':') || path.Contains('/') || path[3..].Split('\\').Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.'))) return false;
        try { full = Path.GetFullPath(path); }
        catch (Exception) { return false; }
        return full.Length >= 3 && full[1] == ':';
    }
}

public sealed record Eligibility(bool IsEligible, string Reason);

/// <summary>Decides whether a queue may go to the real (helper) executor. Anything not eligible is simulated.</summary>
public static class RealExecutionEligibility
{
    public static Eligibility Evaluate(IReadOnlyList<QueuedOperation> items, PreviewState baseline, HelperPolicy policy, bool helperInstalled)
    {
        if (items.Count == 0) return new(false, "There are no tasks.");
        if (policy.Mode == WriteMode.Disabled) return new(false, "Real disk writes are disabled on this PC (helper policy: Disabled).");
        if (!helperInstalled) return new(false, "The privileged helper is not installed.");

        var state = baseline;
        var structurallyChanged = new HashSet<int>();
        foreach (var item in items)
        {
            if (structurallyChanged.Contains(item.Operation.DiskNumber))
                return new(false, $"\"{item.Description}\" follows a structural change on the same disk. Windows assigns new partition numbers and GUIDs that cannot be predicted, so run one structural change per disk, refresh, then continue.");
            var request = WriteRequestFactory.TryCreate(item.Operation, state);
            if (request is null) return new(false, $"\"{item.Description}\" is not yet available as a real operation.");
            if (request.IsStructural) structurallyChanged.Add(item.Operation.DiskNumber);
            var disk = state.Disk(item.Operation.DiskNumber);
            if (request.Kind is WriteOperationKind.MergePartitions)
                return new(false, "Unsupported real operation: Merge is simulation only pending consistent copying and durable recovery.");
            if (request.Kind == WriteOperationKind.MovePartition && MoveRefusal(disk, request) is { } refusal)
                return new(false, $"\"{item.Description}\" will be simulated: {refusal}");
            if (request.Kind == WriteOperationKind.InitializeDisk && !policy.ApprovesInitialization(disk, DateTimeOffset.UtcNow))
                return new(false, "Initialization is blocked without a fresh administrator approval for this newly created blank VHDX.");
            var targetPartition = request.Partition is null ? null : disk.FindPartition(request.Partition.PartitionNumber);
            if (!disk.IsVirtual && policy.AllowsPhysicalTest(disk, request.Kind, targetPartition)) { /* enrolled test disk */ }
            else if (!disk.IsVirtual || !policy.AllowsImage(disk.VirtualImagePath))
                return new(false, $"{disk.Title} is not a virtual disk inside an approved test folder. Physical disks are never written in this version" +
                                  (policy.IsEnrolledPhysicalTestDisk(disk) ? " (on an enrolled test disk the system, EFI, MSR and recovery partitions allow only in-place shrink and extend; Merge and Initialize stay simulated)." : "."));
            if (!WriteRequestValidator.Validate(request).IsValid) return new(false, $"\"{item.Description}\" cannot be sent to the helper.");
            try { state = item.Operation.Apply(state); }
            catch (OperationValidationException ex) { return new(false, ex.Message); }
        }
        return new(true, "Real operation on approved virtual disks; each result will be independently re-enumerated and verified.");
    }

    /// <summary>Mirrors the helper's Move preconditions so the reason is visible before Apply (the helper re-checks).</summary>
    internal static string? MoveRefusal(DiskInfo disk, WriteOperationRequest request)
    {
        var p = disk.FindPartition(request.Partition!.PartitionNumber);
        if (p is null) return null;
        if (p.IsProtectedRole || p.IsExtendedContainer) return "system, recovery and container partitions are never moved.";
        if (p.IsBoot || p.IsSystem) return "the partition hosts the running system.";
        if (p.IsLogical) return "logical partitions inside an MBR extended partition cannot be moved yet.";
        if (!p.HasFileSystem || !PartitionManager.Core.FileSystems.FileSystemRules.CanRelocateData(p.FileSystem)) return "only NTFS, FAT32 and exFAT volumes can be moved for real.";
        if (p.IsEncrypted) return "BitLocker volumes cannot be moved yet.";
        var dest = request.OffsetBytes!.Value;
        if (disk.Partitions.Any(x => x.IsExtendedContainer && dest < x.EndBytes && x.OffsetBytes < dest + p.SizeBytes))
            return "the destination overlaps the MBR extended partition.";
        return null;
    }
}

public sealed record BlankImageApproval(DiskIdentity Disk, DateTimeOffset CreatedUtc);
