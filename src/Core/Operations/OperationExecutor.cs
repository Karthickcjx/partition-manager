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
