using System.Text.Json.Serialization;

namespace PartitionManager.Core.Recovery;

/// <summary>
/// Read-only data-recovery requests carried over the authenticated helper pipe, so the app itself never runs elevated.
/// The helper only READS the source volume/disk; recovered bytes are returned to the app in chunks and the app writes
/// them (as the signed-in user) to a folder on a different drive.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RecoveryAction>))]
public enum RecoveryAction { ScanVolume, ListFiles, ReadFile, ScanLostPartitions }

public sealed record RecoveryRequest(
    RecoveryAction Action,
    int DiskNumber,
    int PartitionNumber = 0,
    long PartitionOffset = 0,
    long PartitionSize = 0,
    int Start = 0,
    int Count = 0,
    long RecordNumber = 0,
    long Position = 0,
    int Length = 0)
{
    public const int MaxListPage = 100;
    public const int MaxReadBytes = 32 * 1024;
}

public sealed record RecoveryScanSummary(long RecordsScanned, long CorruptRecords, int FileCount, double ElapsedMs, int ClusterSize);

public sealed record RecoverableFileInfo(long RecordNumber, string Name, string Folder, long SizeBytes, DateTime? Modified, DateTime? Created, RecoveryChance Chance)
{
    public static RecoverableFileInfo From(RecoverableFile f) => new(f.RecordNumber, f.Name, f.Folder, f.SizeBytes, f.Modified, f.Created, f.Chance);
    /// <summary>A display copy without data runs: the app never needs them, the helper keeps them.</summary>
    public RecoverableFile ToDisplay() => new(RecordNumber, Name, Folder, SizeBytes, Modified, Created, Chance, null);
}

public sealed record RecoveryFilePage(int Start, int Total, IReadOnlyList<RecoverableFileInfo> Files);

public sealed record RecoveryChunk(long Position, int Length, bool EndOfData, string Base64);

public sealed record LostPartitionList(IReadOnlyList<FoundPartition> Partitions, double ElapsedMs);
