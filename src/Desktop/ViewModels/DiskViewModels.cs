using System.Collections.ObjectModel;
using System.Windows;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Models;
using PartitionManager.Desktop.Mvvm;

namespace PartitionManager.Desktop.ViewModels;

public enum SegmentKind { Data, System, Unallocated, Encrypted, Hidden }

/// <summary>One block on the disk map and one row in the partition table.</summary>
public sealed class SegmentViewModel : ObservableObject
{
    private bool _isSelected;

    public SegmentViewModel(DiskViewModel disk, LayoutSegment segment, bool isPending)
    {
        Disk = disk;
        Segment = segment;
        IsPending = isPending;
    }

    public DiskViewModel Disk { get; }
    public LayoutSegment Segment { get; }
    public PartitionInfo? Partition => Segment.Partition;
    public bool IsUnallocated => Segment.IsUnallocated;
    public bool IsPartition => !IsUnallocated;

    /// <summary>True when this block differs from the on-disk layout because of a pending task.</summary>
    public bool IsPending { get; }

    public event EventHandler? Selected;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value) Selected?.Invoke(this, EventArgs.Empty);
        }
    }

    public (int Disk, int? Partition, long Offset) Key => (Segment.DiskNumber, Partition?.PartitionNumber, Segment.OffsetBytes);

    public SegmentKind Kind => Partition switch
    {
        null => SegmentKind.Unallocated,
        { IsEncrypted: true } => SegmentKind.Encrypted,
        { IsProtectedRole: true } => SegmentKind.System,
        { IsHidden: true } => SegmentKind.Hidden,
        _ => SegmentKind.Data,
    };

    public double Weight => Segment.SizeBytes;

    public string Title => Partition switch
    {
        null => "Unallocated",
        { DriveLetter: { } c } => $"{c}:",
        { IsProtectedRole: true } p => ShortRole(p.Role),
        _ => "*:",
    };

    public string Name => Partition switch
    {
        null => "Unallocated",
        { } p when !string.IsNullOrWhiteSpace(p.Label) => p.Label,
        { DriveLetter: not null } => "Local Disk",
        { } p => PartitionTypes.RoleDisplayName(p.Role),
    };

    public string SizeText => ByteSize.Format(Segment.SizeBytes);
    /// <summary>Partitions without a mounted volume (ESP, MSR) have no file system Windows will report, so show "--" rather than guess.</summary>
    public string FileSystemText => Partition is { HasFileSystem: true } p ? p.FileSystem : IsUnallocated ? "" : "--";
    public string MapFileSystemText => Partition is { HasFileSystem: true } p ? p.FileSystem : "";
    public string MapCaption => string.Join(" ", new[] { SizeText, MapFileSystemText }.Where(s => !string.IsNullOrEmpty(s)));
    public string UsedText => ByteSize.Format(Partition?.UsedBytes);
    public string FreeText => Partition is null ? SizeText : ByteSize.Format(Partition.FreeBytes);
    public string TypeText => Partition is null ? "Unallocated" : PartitionTypes.TypeColumnText(Disk.Disk, Partition);

    public double UsedFraction => Partition?.UsedFraction ?? 0;
    public GridLength UsedColumn => new(Math.Max(UsedFraction, 0.0001), GridUnitType.Star);
    public GridLength FreeColumn => new(Math.Max(1 - UsedFraction, 0.0001), GridUnitType.Star);
    public bool ShowUsage => Partition?.UsedFraction is not null;

    public string StatusText
    {
        get
        {
            if (Partition is null) return IsPending ? "Pending" : "";
            var flags = new List<string>();
            if (IsPending) flags.Add("Pending");
            if (Partition.IsBoot) flags.Add("Boot");
            if (Partition.IsSystem) flags.Add("System");
            if (Partition.IsActive) flags.Add("Active");
            if (Partition.IsHidden) flags.Add("Hidden");
            if (Partition.BitLocker == BitLockerState.Encrypted) flags.Add("BitLocker");
            if (Partition.BitLocker == BitLockerState.Locked) flags.Add("Locked");
            return flags.Count == 0 ? "Healthy" : string.Join(", ", flags);
        }
    }

    public string ToolTipText => Partition is null
        ? $"Unallocated space\n{SizeText} at offset {ByteSize.Format(Segment.OffsetBytes)}"
        : $"{Partition.DisplayName}\n{TypeText} · {FileSystemText} · {SizeText}\nUsed {UsedText}, free {FreeText}" + (IsPending ? "\nChanged by a pending task" : "");

    public string AutomationName => Partition is null ? $"Unallocated, {SizeText}" : $"{Partition.DisplayName}, {FileSystemText}, {SizeText}, {StatusText}";

    private static string ShortRole(PartitionRole role) => role switch
    {
        PartitionRole.EfiSystem => "EFI",
        PartitionRole.MicrosoftReserved => "MSR",
        PartitionRole.Recovery => "Recovery",
        _ => "*:",
    };
}

public sealed class DiskViewModel : ObservableObject
{
    public DiskViewModel(DiskInfo disk, DiskInfo? baseline)
    {
        Disk = disk;
        var baseSegments = baseline is null ? [] : DiskLayout.Segments(baseline).ToHashSet();
        Segments = new ObservableCollection<SegmentViewModel>(
            DiskLayout.Segments(disk).Select(s => new SegmentViewModel(this, s, baseline is not null && !baseSegments.Contains(s))));
        HasPendingChanges = baseline is not null && (Segments.Any(s => s.IsPending) || baseline.Style != disk.Style);
    }

    public DiskInfo Disk { get; }
    public ObservableCollection<SegmentViewModel> Segments { get; }
    public bool HasPendingChanges { get; }

    public string Title => Disk.Title;
    public string Model => Disk.Model;
    public string SizeText => ByteSize.Format(Disk.SizeBytes);
    public string StyleText => Disk.Style switch
    {
        PartitionStyle.Gpt => "GPT",
        PartitionStyle.Mbr => "MBR",
        _ => "Not initialized",
    };
    public string MediaText => Disk.Media switch
    {
        MediaKind.Ssd => "SSD",
        MediaKind.Hdd => "HDD",
        MediaKind.Scm => "SCM",
        _ => Disk.IsVirtual ? "Virtual" : "",
    };
    public string Summary => string.Join(" · ", new[] { SizeText, StyleText, MediaText, Disk.BusType, UnallocatedText }.Where(s => !string.IsNullOrEmpty(s) && s != "Unknown"));
    public bool IsSystemDisk => Disk.HostsRunningSystem;
    public bool IsUninitialized => Disk.Style is PartitionStyle.Raw or PartitionStyle.Unknown;
    public bool IsReadOnly => Disk.IsReadOnly;
    public bool IsOffline => Disk.IsOffline;
    public string UnallocatedText => $"{ByteSize.Format(DiskLayout.UnallocatedBytes(Disk))} unallocated";
}
