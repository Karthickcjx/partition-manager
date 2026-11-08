using System.IO;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Operations;
using PartitionManager.Infrastructure.WindowsApi;

namespace PartitionManager.Desktop.Services;

/// <summary>A runnable queue has an executor; a queue that cannot run has none and a reason. Nothing is simulated.</summary>
public sealed record ExecutionRoute(IOperationExecutor? Executor, string Reason);

public interface IExecutionRouter
{
    /// <summary>Text for the status line under the Execute button.</summary>
    string ModeDescription { get; }
    /// <summary>True when the helper policy allows real writes and the helper is installed.</summary>
    bool RealWritesEnabled => false;

    ExecutionRoute Route(IReadOnlyList<QueuedOperation> items, PreviewState baseline);
}

/// <summary>
/// Sends a queue to the privileged helper when every task is allowed; otherwise the queue does not run and the reason
/// is shown. The helper re-checks everything independently before writing.
/// </summary>
public sealed class ExecutionRouter(Func<IOperationExecutor> realExecutor, IHelperPolicySource policy, string helperPath)
    : IExecutionRouter
{
    public bool RealWritesEnabled => policy.Load().Mode != WriteMode.Disabled && File.Exists(helperPath);

    public string ModeDescription => RealWritesEnabled
        ? "Changes are written to the real disks after you confirm. The running Windows, EFI, MSR and recovery partitions can only be shrunk or extended."
        : "Disk writes are disabled on this PC (helper policy missing). Reinstall Partition Manager to restore the default policy.";

    public ExecutionRoute Route(IReadOnlyList<QueuedOperation> items, PreviewState baseline)
    {
        var eligibility = RealExecutionEligibility.Evaluate(items, baseline, policy.Load(), File.Exists(helperPath));
        return eligibility.IsEligible ? new(realExecutor(), eligibility.Reason) : new(null, eligibility.Reason);
    }
}
