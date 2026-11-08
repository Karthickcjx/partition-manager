using System.Diagnostics;
using System.Windows.Input;
using PartitionManager.Core.Discovery;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Operations;
using PartitionManager.Desktop.Mvvm;
using PartitionManager.Desktop.Services;
using PartitionManager.Desktop.ViewModels.Dialogs;
using PartitionManager.Infrastructure.WindowsApi;

namespace PartitionManager.Desktop.ViewModels;

public sealed record NavItem(string Glyph, string Title, object Page, string? Badge = null)
{
    public string AutomationName => Badge is null ? Title : $"{Title} ({Badge})";
}

public sealed class MainViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private NavItem _current;

    public MainViewModel(IDiskDiscoveryService discovery, IExecutionRouter router, IDialogService dialogs, IOperationLog log, string logDirectory, bool demoMode,
        Func<PartitionManager.Infrastructure.Ipc.HelperRecoveryClient>? recoveryClient = null)
    {
        _dialogs = dialogs;
        _router = router;
        LogDirectory = logDirectory;
        IsDemo = demoMode;
        PartitionManager = new PartitionManagerViewModel(discovery, router, dialogs, log);
        DiskInfo = new DiskInfoViewModel(discovery);
        Recovery = new DataRecoveryViewModel(discovery, dialogs, log, pageIndex: 5,
            recoveryClient ?? (() => throw new PartitionManager.Infrastructure.Ipc.HelperUnavailableException("Data recovery needs the privileged helper.")));

        Navigation =
        [
            new("", "Partition Management", PartitionManager),
            new("", "Disk Information", DiskInfo),
            new("", "Clone Disk", new FeaturePageViewModel("Please select the applicable clone mode:",
                "Cloning copies a disk or partition sector-by-sector to another device. This section is not implemented yet.",
                [
                    new("", "Clone OS Disk (System Migration)", "Copy the Windows system disk (ESP, MSR, OS and recovery partitions) to a new disk and make it bootable with bcdboot."),
                    new("", "Clone Data Disk", "Copy every partition of a non-system disk to another disk of equal or larger size."),
                    new("", "Clone Partition", "Copy one partition into unallocated space or over another partition, optionally using its full capacity."),
                ])),
            new("", "Convert Disk", new FeaturePageViewModel("Convert Disk",
                "Convert partition tables and file systems without reformatting. This section is not implemented yet.",
                [
                    new("", "MBR ↔ GPT", "Convert a disk's partition table. Windows' own mbr2gpt.exe covers the system-disk MBR→GPT case."),
                    new("", "NTFS ↔ FAT32", "Convert a volume's file system in place, checking first that files over 4 GB are moved elsewhere."),
                ])),
            new("", "Backup", new FeaturePageViewModel("Backup",
                "Image-based backup and restore. This section is not implemented yet.",
                [
                    new("", "Back up system", "Create a compressed image of the Windows partitions."),
                    new("", "Back up disk or partition", "Image any disk or partition to a file."),
                    new("", "Back up files", "Copy selected folders to an archive."),
                ])),
            new("", "Data Recovery", Recovery),
            new("", "Toolkit", new FeaturePageViewModel("Toolkit",
                "Disk utilities found in the reference application. These are not implemented yet.",
                [
                    new("", "Disk health", "Read SMART attributes and the storage reliability counters."),
                    new("", "Bad sector scan", "Read every sector and report the unreadable ones."),
                    new("", "Boot repair", "Rebuild boot configuration with bcdboot."),
                    new("", "Wipe partition or disk", "Overwrite data so it cannot be recovered."),
                    new("", "Bootable media", "Create a WinPE USB drive that runs this app outside Windows."),
                    new("", "BitLocker tools", "Unlock BitLocker volumes and locate recovery keys."),
                ])),
        ];
        _current = Navigation[0];

        ToggleThemeCommand = new RelayCommand(() => { ThemeManager.Toggle(); Raise(nameof(ThemeGlyph)); });
        OpenLogsCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LogDirectory}\"") { UseShellExecute = true }));
        AboutCommand = new RelayCommand(() => _dialogs.Message("About Partition Manager",
            $"Partition Manager {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3)} (clean-room implementation, preview).\n\n" +
            "Disk information comes from the Windows Storage Management API. Partition operations are queued and previewed first. " +
            (_router.RealWritesEnabled
                ? "Changes are written to the real disks after you confirm. The running Windows, EFI, MSR and recovery partitions can only be shrunk or extended in place; Merge is not available yet.\n\n"
                : "Disk writes are disabled on this PC (helper policy missing); reinstall to restore the default.\n\n") +
            $"Logs: {LogDirectory}"));
    }

    public IReadOnlyList<NavItem> Navigation { get; }
    public PartitionManagerViewModel PartitionManager { get; }
    public DiskInfoViewModel DiskInfo { get; }
    public DataRecoveryViewModel Recovery { get; }
    public string LogDirectory { get; }
    public bool IsDemo { get; }
    public bool IsElevated { get; } = Elevation.IsElevated;
    public string PrivilegeText => IsElevated ? "Administrator" : "Standard user";
    private readonly IExecutionRouter _router;
    // Previously always "Simulation mode", even when real virtual-disk writes were enabled (found in RDP testing).
    public string ModeText => _router.RealWritesEnabled ? "Real disk operations" : "Writes disabled";
    public string ThemeGlyph => ThemeManager.Current == AppTheme.Dark ? "" : "";

    public ICommand ToggleThemeCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand AboutCommand { get; }

    public NavItem Current
    {
        get => _current;
        set
        {
            if (!Set(ref _current, value)) return;
            Raise(nameof(CurrentPage));
            if (value.Page is DiskInfoViewModel info && info.Disks.Count == 0) _ = info.RefreshAsync();
            if (value.Page is DataRecoveryViewModel rec && rec.Volumes.Count == 0) _ = rec.LoadAsync();
        }
    }

    public object CurrentPage => _current.Page;

    public Task InitializeAsync() => PartitionManager.RefreshAsync();
}
