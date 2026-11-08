using System.Text;
using PartitionManager.Core.Logging;

namespace PartitionManager.Infrastructure.Logging;

/// <summary>Appends one line per entry to %LOCALAPPDATA%\PartitionManager\Logs\yyyy-MM-dd.log.</summary>
public sealed class FileOperationLog : IOperationLog
{
    private readonly object _gate = new();

    public FileOperationLog(string? directory = null)
    {
        Directory = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PartitionManager", "Logs");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public string CurrentFile => Path.Combine(Directory, DateTime.Now.ToString("yyyy-MM-dd") + ".log");

    public void Write(LogLevel level, string category, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-7} [{category}] {message.ReplaceLineEndings(" ")}{Environment.NewLine}";
        lock (_gate)
        {
            try { File.AppendAllText(CurrentFile, line, Encoding.UTF8); }
            catch (IOException) { /* logging must never take the app down */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
