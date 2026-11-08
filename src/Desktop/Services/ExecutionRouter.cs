using System.IO;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Operations;
using PartitionManager.Infrastructure.WindowsApi;

namespace PartitionManager.Desktop.Services;

public sealed record ExecutionRoute(IOperationExecutor Executor, string Reason);

public interface IExecutionRouter
{
    /// <summary>Text for the status line under the Execute button.</summary>
    string ModeDescription { get; }
    /// <summary>True when the helper policy allows real writes to approved virtual disks and the helper is installed.</summary>
    bool RealWritesEnabled => false;

    ExecutionRoute Route(IReadOnlyList<QueuedOperation> items, PreviewState baseline);
}

/// <summary>
/// Sends a queue to the privileged helper only when every task is an allowlisted metadata change on an approved
/// virtual disk; everything else is simulated. The helper re-checks all of this independently.
/// </summary>
public sealed class ExecutionRouter(IOperationExecutor simulator, Func<IOperationExecutor> realExecutor, IHelperPolicySource policy, string helperPath)
    : IExecutionRouter
{
    public bool RealWritesEnabled => policy.Load().Mode == WriteMode.VirtualDisksOnly && File.Exists(helperPath);

    public string ModeDescription => RealWritesEnabled
        ? "Real writes are enabled for approved virtual disks and administrator-enrolled test disks only; Merge and all other physical disks stay simulated."
        : "Simulation mode: tasks are rehearsed, never written to disk.";

    public ExecutionRoute Route(IReadOnlyList<QueuedOperation> items, PreviewState baseline)
    {
        var eligibility = RealExecutionEligibility.Evaluate(items, baseline, policy.Load(), File.Exists(helperPath));
        return eligibility.IsEligible ? new(realExecutor(), eligibility.Reason) : new(simulator, eligibility.Reason);
    }
}
