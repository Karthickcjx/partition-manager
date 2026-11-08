using PartitionManager.Core.Execution;
using PartitionManager.PrivilegedHelper.Engine;

namespace PartitionManager.PrivilegedHelper.Ipc;

/// <summary>Decides whether the process on the other end of the pipe is the app instance that launched us.</summary>
public interface IClientVerifier
{
    ValidationResult Verify(HelloMessage hello);
}

/// <summary>
/// One authenticated conversation with one client: HELLO (token + peer check) then a sequence of requests, each
/// checked for replay and freshness before reaching the engine. Any protocol violation ends the session.
/// </summary>
public sealed class HelperSession(Stream stream, IClientVerifier verifier, WriteEngine engine, bool isElevated, string writeMode, TimeProvider? clock = null,
    PartitionManager.PrivilegedHelper.Engine.RecoveryService? recovery = null)
{
    private readonly ReplayGuard _replay = new(clock);

    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    public async Task RunAsync(CancellationToken ct)
    {
        HelloMessage? hello;
        using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            handshake.CancelAfter(HandshakeTimeout);
            try { hello = await HelperProtocol.ReadAsync<HelloMessage>(stream, handshake.Token); }
            catch (ProtocolException ex) { await TrySend(new HelloReply(false, ex.Code, ex.Message, isElevated, writeMode), ct); return; }
            catch (OperationCanceledException) { return; }
        }
        if (hello is null) return;

        var auth = hello.ProtocolVersion != HelperProtocol.Version
            ? ValidationResult.Fail("VERSION", $"Protocol version {hello.ProtocolVersion} is not supported (expected {HelperProtocol.Version}).")
            : verifier.Verify(hello);
        await HelperProtocol.WriteAsync(stream, new HelloReply(auth.IsValid, auth.Code, auth.Message, isElevated, writeMode), ct);
        if (!auth.IsValid) return;

        while (!ct.IsCancellationRequested)
        {
            HelperRequest? request;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                idle.CancelAfter(IdleTimeout);
                try { request = await HelperProtocol.ReadAsync<HelperRequest>(stream, idle.Token); }
                catch (ProtocolException ex) { await TrySend(HelperResponse.Reject(Guid.Empty, ex.Code, ex.Message), ct); return; }
                catch (OperationCanceledException) { return; }
            }
            if (request is null) return;

            var fresh = _replay.Check(request);
            if (!fresh.IsValid)
            {
                await HelperProtocol.WriteAsync(stream, HelperResponse.Reject(request.RequestId, fresh.Code, fresh.Message), ct);
                continue;
            }

            var response = request.Kind switch
            {
                HelperRequestKind.Ping => new HelperResponse(request.RequestId, HelperResponseStatus.Ok, "PONG", "", []),
                HelperRequestKind.Execute when request.Operation is not null => engine.Execute(request.RequestId, request.Operation),
                HelperRequestKind.Execute => HelperResponse.Reject(request.RequestId, "MISSING_OPERATION", "Execute requires an operation."),
                HelperRequestKind.Recovery when recovery is not null => recovery.Handle(request.RequestId, request.Recovery),
                HelperRequestKind.Recovery => HelperResponse.Reject(request.RequestId, "RECOVERY_UNAVAILABLE", "Recovery is not available in this session."),
                _ => HelperResponse.Reject(request.RequestId, "UNKNOWN_KIND", "Unknown request kind."),
            };
            await HelperProtocol.WriteAsync(stream, response, ct);
        }
    }

    private async Task TrySend<T>(T message, CancellationToken ct)
    {
        try { await HelperProtocol.WriteAsync(stream, message, ct); }
        catch (IOException) { }
    }
}
