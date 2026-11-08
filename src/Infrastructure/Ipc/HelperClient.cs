using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using PartitionManager.Core.Execution;

namespace PartitionManager.Infrastructure.Ipc;

public sealed class HelperUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Client end of the helper protocol. <see cref="LaunchAsync"/> starts the elevated helper through UAC with a fresh
/// pipe name and one-time token, checks that the pipe really belongs to the process it started, then authenticates.
/// </summary>
public sealed class HelperClient : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly Process? _process;

    public HelperClient(Stream stream, Process? process = null)
    {
        _stream = stream;
        _process = process;
    }

    public HelloReply? Hello { get; private set; }

    public static async Task<HelperClient> LaunchAsync(string helperPath, CancellationToken ct)
    {
        if (!File.Exists(helperPath)) throw new HelperUnavailableException($"The privileged helper was not found at {helperPath}.");
        var token = RandomNumberGenerator.GetBytes(32);
        var pipeName = "PartitionManager.Helper." + Guid.NewGuid().ToString("N");
        var pid = Environment.ProcessId;

        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(helperPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--pipe {pipeName} --parent {pid} --token {Convert.ToHexString(token)}",
                WindowStyle = ProcessWindowStyle.Hidden,
            }) ?? throw new HelperUnavailableException("The helper did not start.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new HelperUnavailableException("Administrator permission was declined, so nothing was changed.", ex);
        }

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await ConnectWhileRunningAsync(pipe, process, TimeSpan.FromSeconds(30), ct);
            // Defeat pipe squatting: the server end must be the exact process we just started.
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverPid) || serverPid != process.Id)
                throw new HelperUnavailableException("The helper pipe is owned by an unexpected process; refusing to continue.");
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            await pipe.DisposeAsync();
            throw new HelperUnavailableException("Could not connect to the privileged helper.", ex);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }

        var client = new HelperClient(pipe, process);
        await client.HandshakeAsync(token, pid, ct);
        return client;
    }

    /// <summary>
    /// Connects in short slices so that a helper which refuses to start (and exits) is reported at once with its
    /// reason, instead of after the full timeout as a generic connection failure.
    /// </summary>
    internal static async Task ConnectWhileRunningAsync(NamedPipeClientStream pipe, Process process, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                await pipe.ConnectAsync(500, ct);
                return;
            }
            catch (TimeoutException)
            {
                if (TryGetExitCode(process, out var exitCode)) throw new HelperUnavailableException(HelperExitCodes.Describe(exitCode));
                if (DateTime.UtcNow >= deadline) throw;
            }
        }
    }

    private static bool TryGetExitCode(Process process, out int exitCode)
    {
        exitCode = 0;
        try
        {
            if (!process.HasExited) return false;
            exitCode = process.ExitCode;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false; // No usable process handle: fall back to the timeout.
        }
    }

    public async Task HandshakeAsync(byte[] token, int clientPid, CancellationToken ct)
    {
        await HelperProtocol.WriteAsync(_stream, new HelloMessage(HelperProtocol.Version, Convert.ToHexString(token), clientPid), ct);
        Hello = await HelperProtocol.ReadAsync<HelloReply>(_stream, ct) ?? throw new HelperUnavailableException("The helper closed the connection during authentication.");
        if (!Hello.Accepted) throw new HelperUnavailableException($"The helper refused the connection ({Hello.Code}): {Hello.Message}");
    }

    public Task<HelperResponse> SendRecoveryAsync(PartitionManager.Core.Recovery.RecoveryRequest recovery, CancellationToken ct) =>
        SendAsync(HelperRequestKind.Recovery, null, ct, recovery);

    public async Task<HelperResponse> SendAsync(HelperRequestKind kind, WriteOperationRequest? operation, CancellationToken ct,
        PartitionManager.Core.Recovery.RecoveryRequest? recovery = null)
    {
        var request = new HelperRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, kind, operation, recovery);
        await HelperProtocol.WriteAsync(_stream, request, ct);
        var response = await HelperProtocol.ReadAsync<HelperResponse>(_stream, ct)
                       ?? throw new HelperUnavailableException("The helper closed the connection unexpectedly (it may have crashed). Refresh to see the current state.");
        if (response.RequestId != request.RequestId)
            throw new HelperUnavailableException("The helper answered a different request; aborting.");
        return response;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        if (_process is not null)
        {
            // Closing IPC must never terminate a storage call in progress. The helper
            // finishes the current operation, verifies it, then exits on EOF/timeout.
            // Dispose releases our process handle; it does not kill the process.
            _process.Dispose();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
