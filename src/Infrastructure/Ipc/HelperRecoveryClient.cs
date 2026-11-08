using System.Text.Json;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Models;
using PartitionManager.Core.Recovery;

namespace PartitionManager.Infrastructure.Ipc;

/// <summary>
/// Data recovery through the elevated helper, so the app never runs as administrator. The helper is started (one UAC
/// prompt) on first use and the session is kept for the page; recovered bytes arrive in chunks and are written here,
/// as the signed-in user, to the destination the user chose.
/// </summary>
public sealed class HelperRecoveryClient(Func<CancellationToken, Task<HelperClient>> connect) : IAsyncDisposable
{
    private HelperClient? _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private async Task<T> CallAsync<T>(RecoveryRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _client ??= await connect(ct);
            HelperResponse response;
            try { response = await _client.SendRecoveryAsync(request, CancellationToken.None); }
            catch (Exception ex) when (ex is IOException or ProtocolException or HelperUnavailableException)
            {
                await ResetAsync();
                throw new HelperUnavailableException("Lost contact with the privileged helper; nothing was written. Try again.", ex);
            }
            if (response.Status != HelperResponseStatus.Ok || response.Payload is null)
                throw new HelperUnavailableException($"{response.Message} ({response.Code})");
            return JsonSerializer.Deserialize<T>(response.Payload, HelperProtocol.Json) ?? throw new HelperUnavailableException("The helper returned an empty result.");
        }
        finally { _gate.Release(); }
    }

    public Task<RecoveryScanSummary> ScanAsync(DiskInfo disk, PartitionInfo partition, CancellationToken ct) =>
        CallAsync<RecoveryScanSummary>(new(RecoveryAction.ScanVolume, disk.Number, partition.PartitionNumber, partition.OffsetBytes, partition.SizeBytes), ct);

    public async Task<IReadOnlyList<RecoverableFileInfo>> ListAllAsync(IProgress<int>? progress, CancellationToken ct)
    {
        var all = new List<RecoverableFileInfo>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await CallAsync<RecoveryFilePage>(new(RecoveryAction.ListFiles, 0, Start: all.Count, Count: RecoveryRequest.MaxListPage), ct);
            all.AddRange(page.Files);
            progress?.Report(all.Count);
            if (page.Files.Count == 0 || all.Count >= page.Total) return all;
        }
    }

    public Task<IReadOnlyList<FoundPartition>> ScanLostPartitionsAsync(DiskInfo disk, CancellationToken ct) =>
        CallAsync<LostPartitionList>(new(RecoveryAction.ScanLostPartitions, disk.Number), ct).ContinueWith(t => t.Result.Partitions, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    /// <summary>Streams one file's recovered data from the helper into <paramref name="output"/>. Returns bytes written.</summary>
    public async Task<long> CopyFileAsync(RecoverableFileInfo file, Stream output, CancellationToken ct)
    {
        long position = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = await CallAsync<RecoveryChunk>(new(RecoveryAction.ReadFile, 0, RecordNumber: file.RecordNumber, Position: position, Length: RecoveryRequest.MaxReadBytes), ct);
            if (chunk.Position != position) throw new HelperUnavailableException("The helper returned data for a different position.");
            var bytes = Convert.FromBase64String(chunk.Base64);
            if (bytes.Length != chunk.Length) throw new HelperUnavailableException("The helper returned a malformed chunk.");
            await output.WriteAsync(bytes, ct);
            position += bytes.Length;
            if (chunk.EndOfData || bytes.Length == 0) return position;
        }
    }

    private async Task ResetAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        _client = null;
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _gate.Dispose();
    }
}
