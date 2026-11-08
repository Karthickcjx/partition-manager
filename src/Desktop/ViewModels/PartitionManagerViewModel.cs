using System.Collections.ObjectModel;
using System.Windows.Input;
using PartitionManager.Core.Discovery;
using PartitionManager.Core.Layout;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Models;
using PartitionManager.Core.Operations;
using PartitionManager.Core.Safety;
using PartitionManager.Desktop.Mvvm;
using PartitionManager.Desktop.Services;
using PartitionManager.Desktop.ViewModels.Dialogs;

namespace PartitionManager.Desktop.ViewModels;

public sealed record TaskItemViewModel(int Index, string Description, RiskLevel Risk, IReadOnlyList<string> Warnings)
{
    public string Number => $"{Index + 1}.";
    public bool IsDestructive => Risk == RiskLevel.Destructive;
    public bool IsHighRisk => Risk >= RiskLevel.High;
    public string RiskText => Risk switch
    {
        RiskLevel.Destructive => "Erases data",
        RiskLevel.High => "High risk",
        RiskLevel.Medium => "Writes to disk",
        _ => "Metadata only",
    };
    public string WarningText => string.Join("\n", Warnings);
}

public sealed class PartitionManagerViewModel : ObservableObject
{
    private readonly IDiskDiscoveryService _discovery;
    private readonly IExecutionRouter _router;
    private readonly IDialogService _dialogs;
    private readonly IOperationLog _log;
    private readonly SafetyPolicy _policy = new();
    private OperationQueue _queue;
    private SegmentViewModel? _selected;
    private bool _isLoading;
    private bool _isSimulatedLayout;
    private string? _loadError;
    private DateTimeOffset? _lastRefresh;
    private IReadOnlyList<string> _discoveryWarnings = [];

    public PartitionManagerViewModel(IDiskDiscoveryService discovery, IExecutionRouter router, IDialogService dialogs, IOperationLog log)
    {
        _discovery = discovery;
        _router = router;
        _dialogs = dialogs;
        _log = log;
        _queue = NewQueue(new PreviewState([]));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        CreateCommand = new RelayCommand(Create, () => Selected is { IsUnallocated: true } s && !s.Disk.IsUninitialized);
        DeleteCommand = new RelayCommand(Delete, () => Selected?.IsPartition == true);
        ResizeCommand = new RelayCommand(Resize, () => Selected is { Partition: { IsExtendedContainer: false } });
        SplitCommand = new RelayCommand(Split, () => Selected is { Partition: { IsProtectedRole: false, IsExtendedContainer: false } });
        MergeCommand = new RelayCommand(Merge, () => Selected is { Partition: { IsProtectedRole: false, IsExtendedContainer: false } });
        FormatCommand = new RelayCommand(Format, () => Selected is { Partition: { IsExtendedContainer: false } });
        ChangeLetterCommand = new RelayCommand(ChangeLetter, () => Selected?.Partition?.HasFileSystem == true);
        ChangeLabelCommand = new RelayCommand(ChangeLabel, () => Selected?.Partition?.HasFileSystem == true);
        ToggleHiddenCommand = new RelayCommand(ToggleHidden, () => Selected?.IsPartition == true);
        PropertiesCommand = new RelayCommand(Properties, () => Selected is not null);
        InitializeDiskCommand = new RelayCommand(InitializeDisk, () => SelectedDisk?.IsUninitialized == true);
        DeleteAllCommand = new RelayCommand(DeleteAll, () => SelectedDisk is { Disk.Partitions.Count: > 0 });
        UndoCommand = new RelayCommand(Undo, () => _queue.HasPending);
        ClearTasksCommand = new RelayCommand(ClearTasks, () => _queue.HasPending);
        RemoveTaskCommand = new RelayCommand(p => RemoveTask(p as TaskItemViewModel), p => p is TaskItemViewModel);
        ExecuteCommand = new RelayCommand(Execute, () => _queue.HasPending);
    }

    public ObservableCollection<DiskViewModel> Disks { get; } = [];
    public ObservableCollection<TaskItemViewModel> Tasks { get; } = [];

    public ICommand RefreshCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ResizeCommand { get; }
    public ICommand SplitCommand { get; }
    public ICommand MergeCommand { get; }
    public ICommand FormatCommand { get; }
    public ICommand ChangeLetterCommand { get; }
    public ICommand ChangeLabelCommand { get; }
    public ICommand ToggleHiddenCommand { get; }
    public ICommand PropertiesCommand { get; }
    public ICommand InitializeDiskCommand { get; }
    public ICommand DeleteAllCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand ClearTasksCommand { get; }
    public ICommand RemoveTaskCommand { get; }
    public ICommand ExecuteCommand { get; }

    public string ExecutionModeText => _router.ModeDescription;

    public SegmentViewModel? Selected
    {
        get => _selected;
        private set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(SelectedDisk));
            Raise(nameof(SelectedRows));
            Raise(nameof(HideMenuText));
            Raise(nameof(SelectionTitle));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public DiskViewModel? SelectedDisk => _selected?.Disk;
    public IReadOnlyList<SegmentViewModel> SelectedRows => SelectedDisk?.Segments ?? (IReadOnlyList<SegmentViewModel>)[];
    public string SelectionTitle => SelectedDisk is { } d ? $"{d.Title} · {d.Model}" : "Select a disk or partition";
    public string HideMenuText => Selected?.Partition?.IsHidden == true ? "Show" : "Hide";

    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }
    public string? LoadError { get => _loadError; private set => Set(ref _loadError, value); }
    public bool IsSimulatedLayout { get => _isSimulatedLayout; private set => Set(ref _isSimulatedLayout, value); }
    public string TaskTitle => $"Task List ({Tasks.Count})";
    public bool HasTasks => Tasks.Count > 0;
    public string DiscoveryWarnings => string.Join("\n", _discoveryWarnings);
    public string LastRefreshText => _lastRefresh is { } t ? $"Updated {t:HH:mm:ss}" : "";

    private OperationQueue NewQueue(PreviewState baseline)
    {
        var q = new OperationQueue(baseline, _policy);
        q.Changed += (_, _) => Rebuild();
        return q;
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        LoadError = null;
        try
        {
            var result = await _discovery.DiscoverAsync();
            _discoveryWarnings = result.Warnings;
            _lastRefresh = result.Timestamp;
            Raise(nameof(DiscoveryWarnings));
            Raise(nameof(LastRefreshText));
            IsSimulatedLayout = false;
            var dropped = _queue.Rebase(result.State);
            Rebuild();
            if (dropped.Count > 0)
                _dialogs.Message("Tasks removed",
                    "The disk layout changed, so these pending tasks no longer apply and were removed:\n\n" + string.Join("\n", dropped.Select(d => "• " + d.Description)),
                    MessageKind.Warning);
        }
        catch (Exception ex)
        {
            LoadError = "Could not read disk information: " + ex.Message;
            _log.Error("discovery", ex.ToString());
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Rebuild()
    {
        var key = _selected?.Key;
        Selected = null;
        foreach (var s in Disks.SelectMany(d => d.Segments)) s.Selected -= OnSegmentSelected;
        Disks.Clear();

        var baseline = _queue.Baseline;
        foreach (var disk in _queue.Preview.Disks)
        {
            var baseDisk = baseline.Disks.FirstOrDefault(d => d.Number == disk.Number);
            var vm = new DiskViewModel(disk, _queue.HasPending ? baseDisk : null);
            foreach (var s in vm.Segments) s.Selected += OnSegmentSelected;
            Disks.Add(vm);
        }

        Tasks.Clear();
        for (var i = 0; i < _queue.Items.Count; i++)
        {
            var item = _queue.Items[i];
            Tasks.Add(new TaskItemViewModel(i, item.Description, item.Operation.Risk, item.Warnings));
        }
        Raise(nameof(TaskTitle));
        Raise(nameof(HasTasks));

        var all = Disks.SelectMany(d => d.Segments).ToList();
        var restore = key is { } k
            ? all.FirstOrDefault(s => s.Key == k)
              ?? all.FirstOrDefault(s => s.Key.Disk == k.Disk && s.Key.Partition is not null && s.Key.Partition == k.Partition)
              ?? all.FirstOrDefault(s => s.Key.Disk == k.Disk)
            : all.FirstOrDefault(s => s.Partition?.IsBoot == true) ?? all.FirstOrDefault();
        if (restore is not null) restore.IsSelected = true;
    }

    private void OnSegmentSelected(object? sender, EventArgs e)
    {
        if (sender is not SegmentViewModel seg) return;
        foreach (var other in Disks.SelectMany(d => d.Segments))
            if (!ReferenceEquals(other, seg) && other.IsSelected) other.IsSelected = false;
        Selected = seg;
    }

    private QueueResult Submit(DiskOperation op)
    {
        var r = _queue.TryAdd(op);
        if (r.Success) _log.Info("queue", "added: " + _queue.Items[^1].Description);
        return r;
    }

    private void SubmitDirect(DiskOperation op)
    {
        var r = Submit(op);
        if (!r.Success) _dialogs.Message(op.Title, r.Error ?? "The operation is not allowed.", MessageKind.Warning);
    }

    private DiskInfo PreviewDisk => _queue.Preview.Disk(Selected!.Disk.Disk.Number);

    private void Create() => _dialogs.Show(new CreatePartitionDialogViewModel(PreviewDisk, Selected!.Segment, _queue.Preview, Submit));
    private void Resize() => _dialogs.Show(new ResizePartitionDialogViewModel(PreviewDisk, Selected!.Partition!, Submit));
    private void Split() => _dialogs.Show(new SplitPartitionDialogViewModel(PreviewDisk, Selected!.Partition!, _queue.Preview, Submit));
    private void Format() => _dialogs.Show(new FormatPartitionDialogViewModel(PreviewDisk, Selected!.Partition!, Submit));
    private void ChangeLetter() => _dialogs.Show(new ChangeLetterDialogViewModel(Selected!.Partition!, _queue.Preview, Submit));
    private void ChangeLabel() => _dialogs.Show(new ChangeLabelDialogViewModel(Selected!.Partition!, Submit));
    private void InitializeDisk() => _dialogs.Show(new InitializeDiskDialogViewModel(SelectedDisk!.Disk, Submit));

    private void Merge()
    {
        var vm = new MergePartitionsDialogViewModel(PreviewDisk, Selected!.Partition!, Submit);
        if (!vm.HasCandidates)
        {
            _dialogs.Message("Merge partitions", "There is no adjacent data partition that can be merged into this one.", MessageKind.Info);
            return;
        }
        _dialogs.Show(vm);
    }

    private void Delete()
    {
        var p = Selected!.Partition!;
        SubmitDirect(new DeletePartitionOperation(p.DiskNumber, p.PartitionNumber));
    }

    private void DeleteAll()
    {
        var disk = SelectedDisk!.Disk;
        if (_dialogs.Confirm("Delete all partitions", "Are you sure you want to delete all partitions?",
                [$"{disk.Title} · {disk.Model} · {ByteSize.Format(disk.SizeBytes)}", $"{disk.Partitions.Count} partition(s) will be removed when the task list is executed."],
                "Add to task list", danger: true))
            SubmitDirect(new DeleteAllPartitionsOperation(disk.Number));
    }

    private void ToggleHidden()
    {
        var p = Selected!.Partition!;
        char? letter = null;
        if (p.IsHidden && _queue.Preview.AvailableLetters.Count > 0) letter = _queue.Preview.AvailableLetters[0];
        SubmitDirect(new SetPartitionHiddenOperation(p.DiskNumber, p.PartitionNumber, !p.IsHidden, letter));
    }

    private void Properties()
    {
        var s = Selected!;
        var disk = s.Disk.Disk;
        var rows = new List<PropertyRow>
        {
            new("Disk", $"{disk.Title} · {disk.Model}"),
            new("Disk serial number", string.IsNullOrEmpty(disk.SerialNumber) ? "--" : disk.SerialNumber),
            new("Partition table", s.Disk.StyleText),
        };
        if (s.Partition is { } p)
        {
            rows.AddRange(
            [
                new("Partition number", p.PartitionNumber.ToString()),
                new("Drive letter", p.DriveLetter is { } c ? $"{c}:" : "None"),
                new("Volume label", string.IsNullOrEmpty(p.Label) ? "--" : p.Label),
                new("File system", p.HasFileSystem ? p.FileSystem : "RAW / none"),
                new("Partition type", s.TypeText),
                new("Role", PartitionTypes.RoleDisplayName(p.Role)),
                new("GPT type GUID", p.GptType?.ToString() ?? "--"),
                new("MBR type", p.MbrType is { } m ? $"0x{m:X2}" : "--"),
                new("Partition size", $"{ByteSize.Format(p.SizeBytes)} ({p.SizeBytes:N0} bytes)"),
                new("Used / free", $"{s.UsedText} / {s.FreeText}"),
                new("Start offset", $"{ByteSize.Format(p.OffsetBytes)} ({p.OffsetBytes:N0} bytes, LBA {p.OffsetBytes / disk.LogicalSectorSize:N0})"),
                new("1 MB aligned", p.OffsetBytes % DiskLayout.Alignment == 0 ? "Yes" : "No"),
                new("Flags", s.StatusText),
                new("BitLocker", p.BitLocker switch
                {
                    BitLockerState.NotEncrypted => "Not encrypted",
                    BitLockerState.Encrypted => "Encrypted",
                    BitLockerState.Locked => "Locked",
                    _ => "Unknown (requires administrator)",
                }),
                new("Volume path", string.IsNullOrEmpty(p.VolumePath) ? "--" : p.VolumePath),
            ]);
        }
        else
        {
            rows.Add(new("Unallocated size", s.SizeText));
            rows.Add(new("Start offset", $"{ByteSize.Format(s.Segment.OffsetBytes)} ({s.Segment.OffsetBytes:N0} bytes)"));
        }
        var issues = LayoutValidator.Validate(disk)
            .Where(i => s.Partition is null || i.PartitionNumber is null || i.PartitionNumber == s.Partition.PartitionNumber)
            .Select(i => $"{i.Severity}: {i.Message}").ToList();
        _dialogs.Show(new PropertiesDialogViewModel(s.Partition is null ? "Unallocated space" : "Partition properties", rows, issues));
    }

    private void Undo() => ReportDropped(_queue.Undo());

    private void RemoveTask(TaskItemViewModel? task)
    {
        if (task is null) return;
        ReportDropped(_queue.RemoveAt(task.Index));
    }

    private void ReportDropped(IReadOnlyList<QueuedOperation> dropped)
    {
        if (dropped.Count > 0)
            _dialogs.Message("Dependent tasks removed",
                "These tasks depended on the one you removed and were removed too:\n\n" + string.Join("\n", dropped.Select(d => "• " + d.Description)),
                MessageKind.Info);
    }

    private void ClearTasks()
    {
        if (_dialogs.Confirm("Clear task list", $"Remove all {Tasks.Count} pending task(s)? Nothing has been written to disk.", null, "Clear"))
            _queue.Clear();
    }

    private void Execute()
    {
        var items = _queue.Items.ToList();
        var affected = items.Select(i => i.Operation.DiskNumber).Distinct()
            .Select(n => _queue.Baseline.Disks.FirstOrDefault(d => d.Number == n))
            .OfType<DiskInfo>()
            .Select(d => $"Target: {d.Title} · {d.Model} · S/N {(string.IsNullOrEmpty(d.SerialNumber) ? "n/a" : d.SerialNumber)} · {ByteSize.Format(d.SizeBytes)}" +
                         (d.HostsRunningSystem ? " · SYSTEM DISK" : ""))
            .ToList();
        var details = affected
            .Concat(items.Select((i, n) => $"{n + 1}. {i.Description}" + (i.Operation.IsDestructive ? "  [erases data]" : "")))
            .Concat(items.SelectMany(i => i.Warnings).Distinct().Select(w => "⚠ " + w))
            .ToList();
        var route = _router.Route(items, _queue.Baseline);
        foreach (var number in items.Select(i => i.Operation.DiskNumber).Distinct())
            details.AddRange(PartitionManager.Core.Layout.LayoutPreviewRows.Describe(_queue.Baseline.Disk(number), _queue.Preview.Disk(number)));
        var executor = route.Executor;
        string message;
        if (executor.IsSimulation)
        {
            message = "Simulation: the tasks below will be rehearsed step by step. No data will be written to any disk.\n" + route.Reason;
        }
        else
        {
            message = "These changes will be WRITTEN to the virtual disk(s) below. The helper re-checks every task before writing.\n" +
                      "Back up anything important on the target first. Cancel stops before the next task; a task in progress always finishes.";
            details.Add(@"Each task is verified and durably journaled (%ProgramData%\PartitionManager\Audit). Interrupted or failed operations require manual recovery review before further writes.");
        }
        if (!_dialogs.Confirm($"Execute {items.Count} task(s)", message, details, executor.IsSimulation ? "Run simulation" : "Write changes",
                danger: !executor.IsSimulation))
            return;

        var progress = new ProgressDialogViewModel(executor, items, _queue.Baseline);
        _dialogs.Show(progress);
        var report = progress.Report;
        if (report is null) return;

        var fresh = NewQueue(report.Result);
        _queue = fresh;
        IsSimulatedLayout = executor.IsSimulation && report.CompletedOperations > 0;
        Rebuild();
        CommandManager.InvalidateRequerySuggested();

        var elapsed = $"{(int)report.Elapsed.TotalHours}h{report.Elapsed.Minutes}m{report.Elapsed.Seconds}s";
        var suffix = executor.IsSimulation ? "\n\nThis was a simulation. The disk map now shows the simulated result; press Refresh to reload the real layout." : "";
        switch (report.Outcome)
        {
            case ExecutionOutcome.Succeeded:
                _dialogs.Message(executor.IsSimulation ? "Done" : "Completed and verified", $"{report.CompletedOperations} task(s) completed in {elapsed}.{suffix}", MessageKind.Success);
                break;
            case ExecutionOutcome.Cancelled:
                _dialogs.Message("Cancelled", $"Stopped after {report.CompletedOperations} of {items.Count} task(s). Remaining tasks were discarded.{suffix}", MessageKind.Warning);
                break;
            case var _ when report.NothingDispatched:
                _dialogs.Message("Nothing was changed", $"{report.Error}{suffix}", MessageKind.Error);
                break;
            default:
                _dialogs.Message(executor.IsSimulation ? "The task failed" : "Stopped: changes may be partially completed", $"{report.Error}\n\n{report.CompletedOperations} of {items.Count} task(s) completed before the failure. Refresh and inspect the actual layout before retrying.{suffix}", MessageKind.Error);
                break;
        }
        // After real writes, show what is actually on disk rather than the predicted layout.
        if (!executor.IsSimulation) _ = RefreshAsync();
    }
}
