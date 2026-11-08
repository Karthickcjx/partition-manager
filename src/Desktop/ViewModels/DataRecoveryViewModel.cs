using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using PartitionManager.Core.Discovery;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Models;
using PartitionManager.Core.Recovery;
using PartitionManager.Desktop.Mvvm;
using PartitionManager.Desktop.Services;
using PartitionManager.Desktop.ViewModels.Dialogs;
using PartitionManager.Infrastructure.WindowsApi;

namespace PartitionManager.Desktop.ViewModels;

public sealed record VolumeChoice(PartitionInfo Partition, DiskInfo Disk)
{
    public string Text => $"{Partition.DisplayName} · {Partition.FileSystem} · {ByteSize.Format(Partition.SizeBytes)} · {Disk.Title}";
    public override string ToString() => Text;
}

public sealed class RecoverableRow(RecoverableFile file) : ObservableObject
{
    private bool _isChecked;
    public RecoverableFile File { get; } = file;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    public string Name => File.Name;
    public string Folder => File.Folder;
    public string SizeText => ByteSize.Format(File.SizeBytes);
    public long SizeBytes => File.SizeBytes;
    public string ModifiedText => File.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "--";
    public string ChanceText => File.Chance switch
    {
        RecoveryChance.Excellent => "Excellent",
        RecoveryChance.Good => "Good",
        RecoveryChance.Partial => "Partly overwritten",
        RecoveryChance.Overwritten => "Overwritten",
        _ => "Not supported yet",
    };
    public bool IsRecoverable => File.Chance is RecoveryChance.Excellent or RecoveryChance.Good or RecoveryChance.Partial;
}

public sealed record LostPartitionRow(FoundPartition Found)
{
    public string OffsetText => $"{ByteSize.Format(Found.OffsetBytes)} ({Found.OffsetBytes:N0} bytes)";
    public string SizeText => ByteSize.Format(Found.SizeBytes);
    public string FileSystem => Found.FileSystem;
    public string Confidence => Found.Confidence.ToString();
    public string Evidence => Found.Evidence;
}

/// <summary>Deleted-file recovery and lost-partition search. Both only read the source; output goes to another volume.</summary>
public sealed class DataRecoveryViewModel : ObservableObject
{
    private readonly IDiskDiscoveryService _discovery;
    private readonly IDialogService _dialogs;
    private readonly IOperationLog _log;
    private readonly int _pageIndex;
    private CancellationTokenSource? _cts;
    private VolumeChoice? _volume;
    private DiskInfo? _disk;
    private bool _busy;
    private double _progress;
    private string _status = "";
    private string _filter = "";
    private bool _hideUnrecoverable = true;
    private bool _showPartitions;
    private RecoveryScanSummary? _lastScan;
    private VolumeChoice? _scannedVolume;

    private readonly Func<PartitionManager.Infrastructure.Ipc.HelperRecoveryClient> _clientFactory;
    private PartitionManager.Infrastructure.Ipc.HelperRecoveryClient? _client;
    private PartitionManager.Infrastructure.Ipc.HelperRecoveryClient Client => _client ??= _clientFactory();

    public DataRecoveryViewModel(IDiskDiscoveryService discovery, IDialogService dialogs, IOperationLog log, int pageIndex,
        Func<PartitionManager.Infrastructure.Ipc.HelperRecoveryClient> clientFactory)
    {
        _clientFactory = clientFactory;
        _discovery = discovery;
        _dialogs = dialogs;
        _log = log;
        _pageIndex = pageIndex;
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => o is RecoverableRow r
                               && (!_hideUnrecoverable || r.IsRecoverable)
                               && (_filter.Length == 0 || r.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase) || r.Folder.Contains(_filter, StringComparison.OrdinalIgnoreCase));
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !_busy && Volume is not null);
        RecoverCommand = new AsyncRelayCommand(RecoverAsync, () => !_busy && Rows.Any(r => r.IsChecked));
        ScanPartitionsCommand = new AsyncRelayCommand(ScanPartitionsAsync, () => !_busy && Disk is not null);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => _busy);
        SelectAllCommand = new RelayCommand(() => { foreach (var r in RowsView.Cast<RecoverableRow>().Where(r => r.IsRecoverable)) r.IsChecked = true; CommandManager.InvalidateRequerySuggested(); });
        SelectNoneCommand = new RelayCommand(() => { foreach (var r in Rows) r.IsChecked = false; });
        ShowFilesCommand = new RelayCommand(() => ShowPartitions = false);
        ShowPartitionsCommand = new RelayCommand(() => ShowPartitions = true);
    }

    public ObservableCollection<VolumeChoice> Volumes { get; } = [];
    public ObservableCollection<DiskInfo> Disks { get; } = [];
    public ObservableCollection<RecoverableRow> Rows { get; } = [];
    public ObservableCollection<LostPartitionRow> FoundPartitions { get; } = [];
    public ICollectionView RowsView { get; }

    public ICommand RefreshCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand RecoverCommand { get; }
    public ICommand ScanPartitionsCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand ShowFilesCommand { get; }
    public ICommand ShowPartitionsCommand { get; }

    public VolumeChoice? Volume { get => _volume; set => Set(ref _volume, value); }
    public DiskInfo? Disk { get => _disk; set => Set(ref _disk, value); }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool ShowPartitions { get => _showPartitions; set { if (Set(ref _showPartitions, value)) Raise(nameof(ShowFiles)); } }
    public bool ShowFiles => !_showPartitions;
    public string Filter { get => _filter; set { if (Set(ref _filter, value ?? "")) RowsView.Refresh(); } }
    public bool HideUnrecoverable { get => _hideUnrecoverable; set { if (Set(ref _hideUnrecoverable, value)) RowsView.Refresh(); } }

    public async Task LoadAsync()
    {
        var state = (await _discovery.DiscoverAsync()).State;
        Volumes.Clear();
        foreach (var d in state.Disks)
            foreach (var p in d.Partitions.Where(p => p.FileSystem == "NTFS" && !string.IsNullOrEmpty(p.VolumePath)))
                Volumes.Add(new VolumeChoice(p, d));
        Disks.Clear();
        foreach (var d in state.Disks) Disks.Add(d);
        Volume ??= Volumes.FirstOrDefault(v => v.Partition.DriveLetter is not null && !v.Partition.IsBoot) ?? Volumes.FirstOrDefault();
        Disk ??= Disks.FirstOrDefault();
    }

    private async Task ScanAsync()
    {
        var choice = Volume!;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        Rows.Clear();
        Status = $"Scanning {choice.Partition.DisplayName} (approve the administrator prompt to let the helper read the drive)...";
        try
        {
            var summary = await Client.ScanAsync(choice.Disk, choice.Partition, _cts.Token);
            Status = $"Loading {summary.FileCount:N0} results...";
            var files = await Client.ListAllAsync(new Progress<int>(n => Progress = summary.FileCount == 0 ? 0 : 100.0 * n / summary.FileCount), _cts.Token);
            _lastScan = summary;
            _scannedVolume = choice;
            _files = files.ToDictionary(f => f.RecordNumber);
            foreach (var f in files.OrderByDescending(f => f.Modified ?? DateTime.MinValue)) Rows.Add(new RecoverableRow(f.ToDisplay()));
            RowsView.Refresh();
            var recoverable = files.Count(f => f.Chance is RecoveryChance.Excellent or RecoveryChance.Good);
            Status = $"Scanned {summary.RecordsScanned:N0} MFT records in {summary.ElapsedMs / 1000:0.0} s. Found {summary.FileCount:N0} deleted files, {recoverable:N0} with excellent or good chances" +
                     (summary.CorruptRecords > 0 ? $"; {summary.CorruptRecords:N0} damaged records skipped." : ".");
            _log.Info("recovery", $"scan {choice.Partition.DisplayName}: {summary.RecordsScanned} records, {summary.FileCount} deleted, {summary.ElapsedMs:0} ms (helper)");
        }
        catch (OperationCanceledException) { Status = "Scan cancelled."; }
        catch (PartitionManager.Infrastructure.Ipc.HelperUnavailableException ex)
        {
            Status = "Scan failed: " + ex.Message;
            _log.Error("recovery", ex.ToString());
        }
        finally { IsBusy = false; Progress = 0; }
    }

    private Dictionary<long, RecoverableFileInfo> _files = [];

    private async Task RecoverAsync()
    {
        if (_lastScan is null || _scannedVolume is null) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where to save recovered files (must be a different drive)" };
        if (dialog.ShowDialog() != true) return;
        var dest = dialog.FolderName;
        if (VolumeIdentity.IsSameVolume(_scannedVolume.Partition.VolumePath, dest))
        {
            _dialogs.Message("Choose another drive",
                $"Saving to {_scannedVolume.Partition.DisplayName} could overwrite the very files you are recovering. Choose a folder on a different drive.", MessageKind.Warning);
            return;
        }

        var selected = Rows.Where(r => r.IsChecked).Select(r => r.File).ToList();
        IsBusy = true;
        _cts = new CancellationTokenSource();
        var ok = 0; long bytes = 0; var failures = new List<string>();
        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var f = selected[i];
                Progress = 100.0 * i / selected.Count;
                Status = $"Recovering {i + 1} of {selected.Count}: {f.Name}";
                if (!_files.TryGetValue(f.RecordNumber, out var info)) { failures.Add($"{f.FullPath}: not in the last scan"); continue; }
                string? target = null;
                try
                {
                    // Written by the app as the signed-in user; the helper only supplies the bytes.
                    var folder = dest;
                    foreach (var part in f.Folder.Split('\\', StringSplitOptions.RemoveEmptyEntries)) folder = Path.Combine(folder, FileRecoverer.SafeName(part));
                    Directory.CreateDirectory(folder);
                    target = FileRecoverer.UniquePath(Path.Combine(folder, FileRecoverer.SafeName(f.Name)));
                    long written;
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
                        written = await Client.CopyFileAsync(info, output, _cts.Token);
                    if (f.Modified is { } m) try { File.SetLastWriteTimeUtc(target, m); } catch (IOException) { }
                    ok++; bytes += written;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PartitionManager.Infrastructure.Ipc.HelperUnavailableException)
                {
                    failures.Add($"{f.FullPath}: {ex.Message}");
                    if (target is not null) try { if (File.Exists(target) && new FileInfo(target).Length == 0) File.Delete(target); } catch (IOException) { }
                }
            }
        }
        catch (OperationCanceledException) { failures.Add("Cancelled by user."); }
        finally { IsBusy = false; Progress = 0; }

        Status = $"Recovered {ok} of {selected.Count} file(s), {ByteSize.Format(bytes)}, to {dest}.";
        _log.Info("recovery", Status);
        var message = Status + (failures.Count == 0 ? "" : "\n\nNot recovered:\n" + string.Join("\n", failures.Take(10)) + (failures.Count > 10 ? $"\n… and {failures.Count - 10} more" : ""));
        if (_dialogs.Confirm("Recovery finished", message, null, "Open folder"))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dest}\"") { UseShellExecute = true });
    }

    private async Task ScanPartitionsAsync()
    {
        var disk = Disk!;
        IsBusy = true;
        _cts = new CancellationTokenSource();
        FoundPartitions.Clear();
        Status = $"Searching unallocated space on {disk.Title} (approve the administrator prompt if asked)...";
        try
        {
            var sw = Stopwatch.StartNew();
            var found = await Client.ScanLostPartitionsAsync(disk, _cts.Token);
            foreach (var f in found) FoundPartitions.Add(new LostPartitionRow(f));
            Status = found.Count == 0
                ? $"No lost partitions found in the unallocated space of {disk.Title} ({sw.Elapsed.TotalSeconds:0.0} s)."
                : $"Found {found.Count} possible lost partition(s) on {disk.Title} in {sw.Elapsed.TotalSeconds:0.0} s. Restoring them is not implemented yet; their files can be recovered once restored.";
        }
        catch (OperationCanceledException) { Status = "Search cancelled."; }
        catch (PartitionManager.Infrastructure.Ipc.HelperUnavailableException ex)
        {
            Status = "Search failed: " + ex.Message;
        }
        finally { IsBusy = false; Progress = 0; }
    }
}
