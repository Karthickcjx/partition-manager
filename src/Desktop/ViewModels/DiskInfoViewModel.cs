using System.Collections.ObjectModel;
using System.Windows.Input;
using PartitionManager.Core.Discovery;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Models;
using PartitionManager.Core.PartitionTables;
using PartitionManager.Desktop.Mvvm;
using PartitionManager.Desktop.ViewModels.Dialogs;
using PartitionManager.Infrastructure.WindowsApi;

namespace PartitionManager.Desktop.ViewModels;

public sealed class DiskDetailViewModel(DiskInfo disk)
{
    public DiskInfo Disk { get; } = disk;
    public string Title => $"{Disk.Title} · {Disk.Model}";
    public IReadOnlyList<PropertyRow> Rows { get; } =
    [
        new("Model", disk.Model),
        new("Serial number", string.IsNullOrEmpty(disk.SerialNumber) ? "--" : disk.SerialNumber),
        new("Capacity", $"{ByteSize.Format(disk.SizeBytes)} ({disk.SizeBytes:N0} bytes)"),
        new("Bus type", disk.BusType),
        new("Media type", disk.Media switch { MediaKind.Ssd => "SSD", MediaKind.Hdd => "HDD", MediaKind.Scm => "Storage-class memory", _ => "Unknown" }),
        new("Partition table", disk.Style switch { PartitionStyle.Gpt => "GPT (GUID partition table)", PartitionStyle.Mbr => "MBR (master boot record)", _ => "Not initialized" }),
        new("Sector size", $"{disk.LogicalSectorSize} bytes logical / {disk.PhysicalSectorSize} bytes physical"),
        new("Health status", disk.Health.ToString()),
        new("Status", string.Join(", ", new[]
        {
            disk.IsOffline ? "Offline" : "Online",
            disk.IsReadOnly ? "Read-only" : null,
            disk.HostsRunningSystem ? "Hosts running Windows" : null,
            disk.IsVirtual ? "Virtual disk" : null,
        }.OfType<string>())),
        new("Partitions", disk.Partitions.Count.ToString()),
        new("Unallocated", ByteSize.Format(DiskLayout.UnallocatedBytes(disk))),
    ];
    public IReadOnlyList<string> Issues { get; } = LayoutValidator.Validate(disk).Select(i => $"{i.Severity}: {i.Message}").ToList();
    public bool HasIssues => Issues.Count > 0;
}

public sealed class DiskInfoViewModel : ObservableObject
{
    private readonly IDiskDiscoveryService _discovery;
    private DiskDetailViewModel? _selected;
    private string? _verifyResult;
    private bool _busy;

    public DiskInfoViewModel(IDiskDiscoveryService discovery)
    {
        _discovery = discovery;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        VerifyCommand = new AsyncRelayCommand(VerifyAsync, () => Selected is not null && CanReadRaw && !_busy);
    }

    public ObservableCollection<DiskDetailViewModel> Disks { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand VerifyCommand { get; }

    /// <summary>Raw sector reads of \\.\PhysicalDriveN require administrator rights.</summary>
    public bool CanReadRaw { get; } = Elevation.IsElevated;
    public string VerifyHint => CanReadRaw
        ? "Reads the partition table straight from the disk (read-only) and checks GPT CRC32 checksums and the backup header."
        : "Run the app as administrator to verify the partition table from raw sectors.";

    public DiskDetailViewModel? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) VerifyResult = null; }
    }

    public string? VerifyResult { get => _verifyResult; private set => Set(ref _verifyResult, value); }

    public async Task RefreshAsync()
    {
        var number = Selected?.Disk.Number;
        var result = await _discovery.DiscoverAsync();
        Disks.Clear();
        foreach (var d in result.State.Disks) Disks.Add(new DiskDetailViewModel(d));
        Selected = Disks.FirstOrDefault(d => d.Disk.Number == number) ?? Disks.FirstOrDefault();
    }

    private async Task VerifyAsync()
    {
        var disk = Selected!.Disk;
        _busy = true;
        VerifyResult = "Reading partition table...";
        try
        {
            VerifyResult = await Task.Run(() =>
            {
                using var reader = RawDiskReader.Open(disk.Number, disk.LogicalSectorSize, disk.SizeBytes);
                var snap = PartitionTableReader.Read(reader);
                var lines = new List<string> { $"Partition table: {snap.Style}, {snap.Partitions.Count} entr{(snap.Partitions.Count == 1 ? "y" : "ies")}" };
                if (snap.Gpt is { } g)
                {
                    lines.Add($"Disk GUID: {g.DiskGuid}");
                    lines.Add($"Primary header CRC32: {(g.HeaderCrcValid ? "OK" : "MISMATCH")}");
                    lines.Add($"Entry array CRC32: {(snap.GptEntriesCrcValid ? "OK" : "MISMATCH")}");
                    lines.Add($"Backup header: {(snap.BackupGptValid ? "OK" : "problem")}");
                    lines.Add($"Usable LBAs: {g.FirstUsableLba:N0} – {g.LastUsableLba:N0}");
                }
                var windowsView = disk.Partitions.Where(p => !p.IsExtendedContainer).Select(p => (p.OffsetBytes, p.SizeBytes)).OrderBy(x => x.OffsetBytes).ToList();
                var rawView = snap.Partitions.Where(p => p.Role != PartitionRole.Extended)
                    .Select(p => ((long)p.StartLba * snap.SectorSize, (long)p.SectorCount * snap.SectorSize)).OrderBy(x => x.Item1).ToList();
                lines.Add(windowsView.SequenceEqual(rawView)
                    ? "Raw table matches the layout reported by Windows."
                    : "Raw table differs from the layout reported by Windows (a pending change or a refresh may be needed).");
                lines.AddRange(snap.Warnings.Select(w => "Warning: " + w));
                return string.Join("\n", lines);
            });
        }
        catch (Exception ex)
        {
            VerifyResult = "Could not read the disk: " + ex.Message;
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
