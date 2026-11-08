using System.Diagnostics;
using PartitionManager.Core.Logging;

namespace PartitionManager.Core.Operations;

public sealed record ExecutionProgress(int OperationIndex, int OperationCount, string OperationText, string StepText, double Percent);

public enum ExecutionOutcome { Succeeded, Cancelled, Failed }

public sealed record ExecutionReport(ExecutionOutcome Outcome, int CompletedOperations, PreviewState Result, string? Error, TimeSpan Elapsed)
{
    /// <summary>True only when this run provably changed nothing: the helper never started (UAC declined, refused to
    /// start), or it refused the first task before writing (Rejected, or a Move whose progress shows no write).</summary>
    public bool NothingDispatched { get; init; }
}

public interface IOperationExecutor
{
    string Name { get; }

    /// <summary>True when the executor only pretends; the UI must label results accordingly.</summary>
    bool IsSimulation { get; }

    Task<ExecutionReport> ExecuteAsync(IReadOnlyList<QueuedOperation> operations, PreviewState baseline,
        IProgress<ExecutionProgress>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Walks the queue step by step with delays, producing the same progress events a real executor would, without
/// opening any device. Cancellation is honoured between steps; completed operations stay applied in the result.
/// </summary>
public sealed class SimulatedExecutor(IOperationLog log, TimeSpan? stepDelay = null) : IOperationExecutor
{
    private readonly TimeSpan _stepDelay = stepDelay ?? TimeSpan.FromMilliseconds(600);

    public string Name => "Simulation";
    public bool IsSimulation => true;

    /// <summary>Test hook: make the operation at this index fail, to exercise error handling paths.</summary>
    public int? FailAtOperation { get; init; }

    public async Task<ExecutionReport> ExecuteAsync(IReadOnlyList<QueuedOperation> operations, PreviewState baseline,
        IProgress<ExecutionProgress>? progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var state = baseline;
        var plans = new List<IReadOnlyList<string>>();
        {
            var s = baseline;
            foreach (var item in operations)
            {
                plans.Add(item.Operation.GetSteps(s));
                try { s = item.Operation.Apply(s); } catch (OperationValidationException) { break; }
            }
        }
        var totalSteps = Math.Max(1, plans.Sum(p => p.Count) + 1);
        var done = 0;
        log.Info("execute", $"[simulation] starting {operations.Count} operation(s)");

        for (var i = 0; i < operations.Count; i++)
        {
            var item = operations[i];
            var steps = i < plans.Count ? plans[i] : item.Operation.Steps;
            try
            {
                foreach (var step in steps)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new ExecutionProgress(i, operations.Count, item.Description, step, 100.0 * done / totalSteps));
                    await Task.Delay(_stepDelay, cancellationToken).ConfigureAwait(false);
                    done++;
                }
                if (FailAtOperation == i)
                    throw new OperationValidationException("Simulated failure requested by test.");
                state = item.Operation.Apply(state);
                log.Info("execute", $"[simulation] done: {item.Description}");
            }
            catch (OperationCanceledException)
            {
                log.Warn("execute", $"[simulation] cancelled before completing: {item.Description}");
                return new ExecutionReport(ExecutionOutcome.Cancelled, i, state, "Cancelled by user.", sw.Elapsed);
            }
            catch (OperationValidationException ex)
            {
                log.Error("execute", $"[simulation] failed: {item.Description}: {ex.Message}");
                return new ExecutionReport(ExecutionOutcome.Failed, i, state, ex.Message, sw.Elapsed);
            }
        }

        progress?.Report(new ExecutionProgress(operations.Count, operations.Count, "", "Done", 100));
        log.Info("execute", $"[simulation] finished in {sw.Elapsed}");
        return new ExecutionReport(ExecutionOutcome.Succeeded, operations.Count, state, null, sw.Elapsed);
    }
}
