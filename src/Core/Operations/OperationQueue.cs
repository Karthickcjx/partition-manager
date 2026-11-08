using PartitionManager.Core.Safety;

namespace PartitionManager.Core.Operations;

public sealed record QueuedOperation(DiskOperation Operation, string Description, IReadOnlyList<string> Warnings);

public sealed record QueueResult(bool Success, string? Error, IReadOnlyList<string> Warnings)
{
    public static QueueResult Ok(IReadOnlyList<string> warnings) => new(true, null, warnings);
    public static QueueResult Fail(string error) => new(false, error, []);
}

/// <summary>
/// Ordered list of pending operations plus the preview they produce. Nothing here touches a disk; the queue is
/// handed to an <see cref="IOperationExecutor"/> only when the user presses Execute.
/// </summary>
public sealed class OperationQueue(PreviewState baseline, SafetyPolicy policy)
{
    private readonly List<QueuedOperation> _items = [];

    public PreviewState Baseline { get; private set; } = baseline;
    public PreviewState Preview { get; private set; } = baseline;
    public IReadOnlyList<QueuedOperation> Items => _items;
    public bool HasPending => _items.Count > 0;

    public event EventHandler? Changed;

    public QueueResult TryAdd(DiskOperation op)
    {
        var verdict = policy.Evaluate(op, Preview);
        if (!verdict.IsAllowed) return QueueResult.Fail(verdict.Blockers[0]);
        try
        {
            var description = op.Describe(Preview);
            var next = op.Apply(Preview);
            _items.Add(new QueuedOperation(op, description, verdict.Warnings));
            Preview = next;
        }
        catch (OperationValidationException ex)
        {
            return QueueResult.Fail(ex.Message);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return QueueResult.Ok(verdict.Warnings);
    }

    /// <summary>Removes an operation and replays the rest. Returns operations that became invalid and were dropped too.</summary>
    public IReadOnlyList<QueuedOperation> RemoveAt(int index)
    {
        if (index < 0 || index >= _items.Count) throw new ArgumentOutOfRangeException(nameof(index));
        _items.RemoveAt(index);
        var dropped = Replay();
        Changed?.Invoke(this, EventArgs.Empty);
        return dropped;
    }

    public IReadOnlyList<QueuedOperation> Undo() => _items.Count == 0 ? [] : RemoveAt(_items.Count - 1);

    public void Clear()
    {
        _items.Clear();
        Preview = Baseline;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Swaps in a freshly discovered baseline (after refresh or execution) and replays what still applies.</summary>
    public IReadOnlyList<QueuedOperation> Rebase(PreviewState newBaseline)
    {
        Baseline = newBaseline;
        var dropped = Replay();
        Changed?.Invoke(this, EventArgs.Empty);
        return dropped;
    }

    private List<QueuedOperation> Replay()
    {
        var dropped = new List<QueuedOperation>();
        var kept = new List<QueuedOperation>();
        var state = Baseline;
        foreach (var item in _items)
        {
            try
            {
                if (!policy.Evaluate(item.Operation, state).IsAllowed) { dropped.Add(item); continue; }
                var next = item.Operation.Apply(state);
                kept.Add(item with { Description = item.Operation.Describe(state) });
                state = next;
            }
            catch (OperationValidationException)
            {
                dropped.Add(item);
            }
        }
        _items.Clear();
        _items.AddRange(kept);
        Preview = state;
        return dropped;
    }
}
