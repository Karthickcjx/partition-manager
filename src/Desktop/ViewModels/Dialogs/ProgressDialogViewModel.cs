using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using PartitionManager.Core.Operations;
using PartitionManager.Desktop.Mvvm;

namespace PartitionManager.Desktop.ViewModels.Dialogs;

/// <summary>Runs the queue through an executor while showing step, percentage and elapsed time; supports cancellation.</summary>
public sealed class ProgressDialogViewModel : DialogViewModel
{
    private readonly IOperationExecutor _executor;
    private readonly IReadOnlyList<QueuedOperation> _operations;
    private readonly PreviewState _baseline;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private string _operationText = "Preparing...";
    private string _stepText = "";
    private double _percent;
    private string _counter = "";
    private bool _cancelling;

    public ProgressDialogViewModel(IOperationExecutor executor, IReadOnlyList<QueuedOperation> operations, PreviewState baseline)
    {
        _executor = executor;
        _operations = operations;
        _baseline = baseline;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Raise(nameof(ElapsedText)), Dispatcher.CurrentDispatcher);
        CancelCommand = new RelayCommand(() => { _cancelling = true; _cts.Cancel(); Raise(nameof(IsCancelling)); }, () => !_cancelling);
    }

    public override string Title => _executor.IsSimulation ? "Executing (simulation)" : "Real operation on virtual disk";
    public override bool ShowFooter => false;
    public bool IsSimulation => _executor.IsSimulation;
    public string CancelText => _executor.IsSimulation ? "Cancel" : "Cancel remaining tasks";
    public string CancelNote => _executor.IsSimulation ? "" : "The task in progress always runs to completion and is verified; cancelling skips the tasks after it.";

    public string OperationText { get => _operationText; private set => Set(ref _operationText, value); }
    public string StepText { get => _stepText; private set => Set(ref _stepText, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public string Counter { get => _counter; private set => Set(ref _counter, value); }
    public bool IsCancelling => _cancelling;
    public string ElapsedText
    {
        get
        {
            var e = _clock.Elapsed;
            return $"Elapsed time: {(int)e.TotalHours}h{e.Minutes}m{e.Seconds}s";
        }
    }

    public ICommand CancelCommand { get; }
    public ExecutionReport? Report { get; private set; }

    public async Task RunAsync()
    {
        _clock.Start();
        _timer.Start();
        var progress = new Progress<ExecutionProgress>(p =>
        {
            Percent = p.Percent;
            if (!string.IsNullOrEmpty(p.OperationText)) OperationText = p.OperationText;
            StepText = p.StepText;
            Counter = p.OperationIndex < p.OperationCount ? $"Task {p.OperationIndex + 1} of {p.OperationCount}" : $"{p.OperationCount} of {p.OperationCount}";
        });
        try
        {
            Report = await _executor.ExecuteAsync(_operations, _baseline, progress, _cts.Token);
        }
        catch (Exception ex)
        {
            Report = new ExecutionReport(ExecutionOutcome.Failed, 0, _baseline, ex.Message, _clock.Elapsed);
        }
        finally
        {
            _timer.Stop();
            _clock.Stop();
        }
        RequestClose(true);
    }
}
