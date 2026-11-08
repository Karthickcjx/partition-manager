using System.Diagnostics;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Operations;

namespace PartitionManager.Infrastructure.Ipc;

/// <summary>
/// Sends allowlisted metadata operations to the privileged helper, one at a time. Cancellation semantics are
/// "before execution only": a cancel stops before the next task; the task already sent always runs to completion.
/// </summary>
public sealed class HelperExecutor(Func<CancellationToken, Task<HelperClient>> connect, IOperationLog log) : IOperationExecutor
{
    public string Name => "Privileged helper (virtual disks only)";
    public bool IsSimulation => false;

    public async Task<ExecutionReport> ExecuteAsync(IReadOnlyList<QueuedOperation> operations, PreviewState baseline,
        IProgress<ExecutionProgress>? progress, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var state = baseline;
        HelperClient client;
        try
        {
            progress?.Report(new ExecutionProgress(0, operations.Count, "Starting privileged helper", "Waiting for administrator approval...", 0));
            client = await connect(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new(ExecutionOutcome.Cancelled, 0, state, "Cancelled before anything was changed.", sw.Elapsed);
        }
        catch (HelperUnavailableException ex)
        {
            log.Warn("execute", "helper unavailable: " + ex.Message);
            return new(ExecutionOutcome.Failed, 0, state, ex.Message, sw.Elapsed) { NothingDispatched = true };
        }

        await using (client)
        {
            for (var i = 0; i < operations.Count; i++)
            {
                var item = operations[i];
                if (cancellationToken.IsCancellationRequested)
                    return new(ExecutionOutcome.Cancelled, i, state, $"Cancelled before task {i + 1}. Earlier tasks were applied.", sw.Elapsed);

                var request = WriteRequestFactory.TryCreate(item.Operation, state);
                if (request is null)
                    return new(ExecutionOutcome.Failed, i, state, $"\"{item.Description}\" cannot be executed by the helper.", sw.Elapsed);

                progress?.Report(new ExecutionProgress(i, operations.Count, item.Description, "Revalidating and writing...", 100.0 * i / operations.Count));
                log.Info("execute", $"[helper] sending {request.Kind} for {item.Description}");
                HelperResponse response;
                try
                {
                    // Not cancellable once sent: the helper finishes and verifies the single metadata change.
                    response = await client.SendAsync(HelperRequestKind.Execute, request, CancellationToken.None);
                }
                catch (Exception ex) when (ex is HelperUnavailableException or IOException or ProtocolException)
                {
                    log.Error("execute", "[helper] connection lost: " + ex.Message);
                    return new(ExecutionOutcome.Failed, i, state,
                        $"Lost contact with the helper during \"{item.Description}\". That task may or may not have been applied; refresh to check.", sw.Elapsed);
                }

                if (response.Status != HelperResponseStatus.Ok)
                {
                    log.Warn("execute", $"[helper] {response.Status} {response.Code}: {response.Message}");
                    // Rejected (or a Move the helper proved never started) means this task changed nothing; if it was the
                    // first task, the whole run changed nothing and the UI must not say "may be partially completed".
                    var unchanged = response.Status == HelperResponseStatus.Rejected || response.Code == "MOVE_NOT_STARTED";
                    var prefix = unchanged ? "Refused, nothing was changed" : "Failed";
                    return new(ExecutionOutcome.Failed, i, state, $"{prefix} ({response.Code}): {response.Message}", sw.Elapsed) { NothingDispatched = unchanged && i == 0 };
                }
                log.Info("execute", $"[helper] done: {response.Message}");
                state = item.Operation.Apply(state);
            }
        }
        progress?.Report(new ExecutionProgress(operations.Count, operations.Count, "", "Done", 100));
        return new(ExecutionOutcome.Succeeded, operations.Count, state, null, sw.Elapsed);
    }
}
