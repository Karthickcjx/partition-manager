namespace PartitionManager.Core.Logging;

public enum LogLevel { Debug, Info, Warning, Error }

public interface IOperationLog
{
    void Write(LogLevel level, string category, string message);
}

public static class OperationLogExtensions
{
    public static void Info(this IOperationLog log, string category, string message) => log.Write(LogLevel.Info, category, message);
    public static void Warn(this IOperationLog log, string category, string message) => log.Write(LogLevel.Warning, category, message);
    public static void Error(this IOperationLog log, string category, string message) => log.Write(LogLevel.Error, category, message);
}

public sealed class NullOperationLog : IOperationLog
{
    public static NullOperationLog Instance { get; } = new();
    public void Write(LogLevel level, string category, string message) { }
}

public sealed class MemoryOperationLog : IOperationLog
{
    private readonly List<(LogLevel Level, string Category, string Message)> _entries = [];
    public IReadOnlyList<(LogLevel Level, string Category, string Message)> Entries { get { lock (_entries) return _entries.ToList(); } }
    public void Write(LogLevel level, string category, string message) { lock (_entries) _entries.Add((level, category, message)); }
}
