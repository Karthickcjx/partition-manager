using System.IO.Pipes;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Logging;
using PartitionManager.Core.Operations;
using PartitionManager.Core.Safety;
using PartitionManager.Infrastructure.Ipc;
using PartitionManager.PrivilegedHelper;
using PartitionManager.PrivilegedHelper.Ipc;
using Xunit;
using static PartitionManager.Tests.Fixtures;
using static PartitionManager.Tests.Production.ExecutionFixtures;

namespace PartitionManager.Tests.Production;

/// <summary>Runs a real helper session and client over an in-process named pipe with a fake storage backend.</summary>
internal sealed class SessionHarness : IAsyncDisposable
{
    public static readonly byte[] Token = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    public const int ClientPid = 4242;
    public const string ClientImage = @"C:\Program Files\PartitionManager\PartitionManager.exe";

    private readonly NamedPipeServerStream _server;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(20));
    public NamedPipeClientStream ClientPipe { get; }
    public Task SessionTask { get; }
    public EngineHarness Engine { get; }

    public SessionHarness(int actualPid = ClientPid, string actualImage = ClientImage, params Core.Models.DiskInfo[] disks)
    {
        Engine = new EngineHarness(disks.Length == 0 ? [VirtualDisk()] : disks);
        var name = "PartitionManager.Helper.test-" + Guid.NewGuid().ToString("N");
        _server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        ClientPipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var verifier = new LaunchingClientVerifier(Token, ClientPid, ClientImage, () => actualPid, _ => actualImage);
        SessionTask = Task.Run(async () =>
        {
            await _server.WaitForConnectionAsync(_cts.Token);
            await new HelperSession(_server, verifier, Engine.Engine, true, "VirtualDisksOnly").RunAsync(_cts.Token);
            _server.Disconnect();
        });
        ClientPipe.Connect(5000);
    }

    public async Task<HelperClient> AuthenticatedClient()
    {
        var client = new HelperClient(ClientPipe);
        await client.HandshakeAsync(Token, ClientPid, CancellationToken.None);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await SessionTask; } catch { }
        await ClientPipe.DisposeAsync();
        await _server.DisposeAsync();
    }
}

public class IpcSessionTests
{
    [Fact]
    public async Task Authenticated_client_can_ping_and_execute()
    {
        await using var h = new SessionHarness();
        var client = await h.AuthenticatedClient();
        Assert.True(client.Hello!.Accepted);
        Assert.Equal("PONG", (await client.SendAsync(HelperRequestKind.Ping, null, CancellationToken.None)).Code);
        var resp = await client.SendAsync(HelperRequestKind.Execute, LabelRequest(VirtualDisk(), "ViaPipe"), CancellationToken.None);
        Assert.Equal(HelperResponseStatus.Ok, resp.Status);
        Assert.Equal("ViaPipe", h.Engine.Discovery.State.Disk(3).Partitions[0].Label);
    }

    [Fact]
    public async Task Wrong_token_is_refused_and_session_ends()
    {
        await using var h = new SessionHarness();
        var client = new HelperClient(h.ClientPipe);
        var ex = await Assert.ThrowsAsync<HelperUnavailableException>(() => client.HandshakeAsync(new byte[32], SessionHarness.ClientPid, CancellationToken.None));
        Assert.Contains("BAD_TOKEN", ex.Message);
        await h.SessionTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(h.Engine.Backend.Calls);
    }

    [Fact]
    public async Task Short_token_is_refused()
    {
        await using var h = new SessionHarness();
        var ex = await Assert.ThrowsAsync<HelperUnavailableException>(() => new HelperClient(h.ClientPipe).HandshakeAsync(SessionHarness.Token[..16], SessionHarness.ClientPid, CancellationToken.None));
        Assert.Contains("BAD_TOKEN", ex.Message);
    }

    [Fact]
    public async Task Other_process_with_stolen_token_is_refused()
    {
        await using var h = new SessionHarness(actualPid: 999);
        var ex = await Assert.ThrowsAsync<HelperUnavailableException>(() => h.AuthenticatedClient());
        Assert.Contains("WRONG_PEER", ex.Message);
    }

    [Fact]
    public async Task Claimed_pid_must_match_real_pid()
    {
        await using var h = new SessionHarness();
        var ex = await Assert.ThrowsAsync<HelperUnavailableException>(() => new HelperClient(h.ClientPipe).HandshakeAsync(SessionHarness.Token, 1, CancellationToken.None));
        Assert.Contains("WRONG_PEER", ex.Message);
    }

    [Fact]
    public async Task Impostor_executable_is_refused()
    {
        await using var h = new SessionHarness(actualImage: @"C:\Users\Public\evil.exe");
        var ex = await Assert.ThrowsAsync<HelperUnavailableException>(() => h.AuthenticatedClient());
        Assert.Contains("WRONG_IMAGE", ex.Message);
    }

    [Fact]
    public async Task Requests_before_authentication_are_not_executed()
    {
        await using var h = new SessionHarness();
        // Skip HELLO and send an execute request straight away: it fails to parse as HELLO and the session ends.
        await HelperProtocol.WriteAsync(h.ClientPipe, new HelperRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, HelperRequestKind.Execute, LabelRequest(VirtualDisk())), CancellationToken.None);
        var reply = await HelperProtocol.ReadAsync<HelloReply>(h.ClientPipe, CancellationToken.None);
        Assert.False(reply!.Accepted);
        Assert.Empty(h.Engine.Backend.Calls);
    }

    [Fact]
    public async Task Replayed_request_is_refused()
    {
        await using var h = new SessionHarness();
        await h.AuthenticatedClient();
        var request = new HelperRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, HelperRequestKind.Execute, LabelRequest(VirtualDisk(), "Once"));
        await HelperProtocol.WriteAsync(h.ClientPipe, request, CancellationToken.None);
        Assert.Equal(HelperResponseStatus.Ok, (await HelperProtocol.ReadAsync<HelperResponse>(h.ClientPipe, CancellationToken.None))!.Status);
        await HelperProtocol.WriteAsync(h.ClientPipe, request, CancellationToken.None);
        var second = await HelperProtocol.ReadAsync<HelperResponse>(h.ClientPipe, CancellationToken.None);
        Assert.Equal("REPLAY", second!.Code);
        Assert.Single(h.Engine.Backend.Calls);
    }

    [Fact]
    public async Task Malformed_frame_ends_the_session()
    {
        await using var h = new SessionHarness();
        await h.AuthenticatedClient();
        await h.ClientPipe.WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F });
        await h.ClientPipe.FlushAsync();
        var reply = await HelperProtocol.ReadAsync<HelperResponse>(h.ClientPipe, CancellationToken.None);
        Assert.Equal("BAD_LENGTH", reply!.Code);
        await h.SessionTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Execute_without_operation_is_refused()
    {
        await using var h = new SessionHarness();
        var client = await h.AuthenticatedClient();
        Assert.Equal("MISSING_OPERATION", (await client.SendAsync(HelperRequestKind.Execute, null, CancellationToken.None)).Code);
    }
}

public class HelperExecutorTests
{
    private static (OperationQueue Queue, PreviewState State) Queue(params DiskOperation[] ops)
    {
        var s = State(VirtualDisk());
        var q = new OperationQueue(s, new SafetyPolicy());
        foreach (var op in ops) Assert.True(q.TryAdd(op).Success);
        return (q, s);
    }

    [Fact]
    public async Task Executes_each_task_through_the_helper()
    {
        await using var h = new SessionHarness();
        var (q, s) = Queue(new ChangeLabelOperation(3, 2, "First"), new ChangeDriveLetterOperation(3, 2, 'R'));
        var exec = new HelperExecutor(_ => h.AuthenticatedClient(), NullOperationLog.Instance);
        var report = await exec.ExecuteAsync(q.Items, s, null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Succeeded, report.Outcome);
        Assert.Equal(["label 3/2 First", "remove 3/2 Q", "add 3/2 R"], h.Engine.Backend.Calls);
        Assert.False(exec.IsSimulation);
    }

    /// <summary>Found in the RDP UAC checks: a helper start refusal was titled "changes may be partially completed".</summary>
    [Fact]
    public async Task Helper_start_failure_is_reported_as_nothing_dispatched()
    {
        var (q, s) = Queue(new ChangeLabelOperation(3, 2, "First"));
        var report = await new HelperExecutor(_ => throw new HelperUnavailableException(HelperExitCodes.Describe(HelperExitCodes.UnsafeEnvironment)), NullOperationLog.Instance)
            .ExecuteAsync(q.Items, s, null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Failed, report.Outcome);
        Assert.True(report.NothingDispatched);
        Assert.Equal(HelperExitCodes.Describe(69), report.Error);
    }

    [Fact]
    public async Task Refusal_of_the_first_task_is_reported_as_nothing_changed()
    {
        await using var h = new SessionHarness();
        var (q, s) = Queue(new ChangeDriveLetterOperation(3, 2, 'R'));
        h.Engine.Discovery.State = h.Engine.Discovery.State with { ExternallyUsedLetters = new HashSet<char> { 'R' } };
        var report = await new HelperExecutor(_ => h.AuthenticatedClient(), NullOperationLog.Instance).ExecuteAsync(q.Items, s, null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Failed, report.Outcome);
        Assert.True(report.NothingDispatched);     // found in review: was titled "changes may be partially completed"
    }

    [Fact]
    public async Task Refusal_after_an_applied_task_is_not_reported_as_nothing_changed()
    {
        await using var h = new SessionHarness();
        var (q, s) = Queue(new ChangeLabelOperation(3, 2, "First"), new ChangeDriveLetterOperation(3, 2, 'R'));
        h.Engine.Discovery.State = h.Engine.Discovery.State with { ExternallyUsedLetters = new HashSet<char> { 'R' } };
        var report = await new HelperExecutor(_ => h.AuthenticatedClient(), NullOperationLog.Instance).ExecuteAsync(q.Items, s, null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Failed, report.Outcome);
        Assert.Equal(1, report.CompletedOperations);
        Assert.False(report.NothingDispatched);
    }

    [Fact]
    public async Task Rejection_stops_the_queue()
    {
        await using var h = new SessionHarness();
        var (q, s) = Queue(new ChangeDriveLetterOperation(3, 2, 'R'), new ChangeLabelOperation(3, 2, "Second"));
        h.Engine.Discovery.State = h.Engine.Discovery.State with { ExternallyUsedLetters = new HashSet<char> { 'R' } };
        var report = await new HelperExecutor(_ => h.AuthenticatedClient(), NullOperationLog.Instance).ExecuteAsync(q.Items, s, null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Failed, report.Outcome);
        Assert.Equal(0, report.CompletedOperations);
        Assert.Contains("nothing was changed", report.Error);
        Assert.Empty(h.Engine.Backend.Calls);
    }

    [Fact]
    public async Task Cancellation_takes_effect_before_the_next_task_only()
    {
        await using var h = new SessionHarness();
        var (q, s) = Queue(new ChangeLabelOperation(3, 2, "First"), new ChangeLabelOperation(3, 2, "Second"));
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(p => { if (p.OperationIndex == 0 && p.StepText.StartsWith("Revalidating")) cts.Cancel(); });
        var report = await new HelperExecutor(_ => h.AuthenticatedClient(), NullOperationLog.Instance).ExecuteAsync(q.Items, s, progress, cts.Token);
        Assert.Equal(ExecutionOutcome.Cancelled, report.Outcome);
        Assert.Equal(1, report.CompletedOperations);
        Assert.Equal(["label 3/2 First"], h.Engine.Backend.Calls);
    }

    [Fact]
    public async Task Declined_elevation_changes_nothing()
    {
        var (q, s) = Queue(new ChangeLabelOperation(3, 2, "First"));
        var report = await new HelperExecutor(_ => throw new HelperUnavailableException("Administrator permission was declined, so nothing was changed."), NullOperationLog.Instance)
            .ExecuteAsync(q.Items, s, null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Failed, report.Outcome);
        Assert.Equal(0, report.CompletedOperations);
        Assert.Same(s, report.Result);
    }

    [Fact]
    public async Task Helper_crash_mid_queue_is_reported_as_uncertain()
    {
        var (q, s) = Queue(new ChangeLabelOperation(3, 2, "First"));
        var crashed = new MemoryStream(); // the helper end is gone: reads hit end-of-stream
        var report = await new HelperExecutor(_ => Task.FromResult(new HelperClient(crashed)), NullOperationLog.Instance).ExecuteAsync(q.Items, s, null, CancellationToken.None);
        Assert.Equal(ExecutionOutcome.Failed, report.Outcome);
        Assert.Contains("may or may not have been applied", report.Error);
    }

    private sealed class SyncProgress(Action<ExecutionProgress> a) : IProgress<ExecutionProgress>
    {
        public void Report(ExecutionProgress value) => a(value);
    }
}

public class HelperArgumentTests
{
    private const string Token = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";

    [Fact]
    public void Valid_arguments_parse()
    {
        Assert.True(Program.TryParse(["--pipe", "PartitionManager.Helper.0123abcd", "--parent", "1234", "--token", Token], out var pipe, out var pid, out var token));
        Assert.Equal("PartitionManager.Helper.0123abcd", pipe);
        Assert.Equal(1234, pid);
        Assert.Equal(32, token.Length);
    }

    [Theory]
    [InlineData("--pipe", @"..\..\pipe\evil", "--parent", "1", "--token", Token)]
    [InlineData("--pipe", "OtherApp.Pipe", "--parent", "1", "--token", Token)]
    [InlineData("--pipe", "PartitionManager.Helper.ok", "--parent", "-5", "--token", Token)]
    [InlineData("--pipe", "PartitionManager.Helper.abc", "--parent", "1", "--token", "short")]
    [InlineData("--pipe", "PartitionManager.Helper.abc", "--pipe", "PartitionManager.Helper.def", "--token", Token)]
    [InlineData("--pipe", "PartitionManager.Helper.abc", "--parent", "1", "--write-physical", "yes")]
    public void Bad_arguments_are_rejected(params string[] args) => Assert.False(Program.TryParse(args, out _, out _, out _));
}

public class InstallLocationTests
{
    private static System.Security.AccessControl.FileSystemAccessRule Allow(System.Security.Principal.WellKnownSidType sid, System.Security.AccessControl.FileSystemRights rights) =>
        new(new System.Security.Principal.SecurityIdentifier(sid, null), rights, System.Security.AccessControl.AccessControlType.Allow);

    [Fact]
    public void Program_files_style_acl_is_accepted()
    {
        var rules = new[]
        {
            Allow(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.AccessControl.FileSystemRights.FullControl),
            Allow(System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.AccessControl.FileSystemRights.FullControl),
            Allow(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, System.Security.AccessControl.FileSystemRights.ReadAndExecute),
        };
        Assert.True(PartitionManager.PrivilegedHelper.Engine.InstallLocationCheck.Evaluate(rules, @"C:\Program Files\PartitionManager").IsValid);
    }

    [Theory]
    [InlineData(System.Security.Principal.WellKnownSidType.BuiltinUsersSid)]
    [InlineData(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid)]
    [InlineData(System.Security.Principal.WellKnownSidType.WorldSid)]
    public void User_writable_folder_is_rejected(System.Security.Principal.WellKnownSidType who)
    {
        var rules = new[] { Allow(who, System.Security.AccessControl.FileSystemRights.Modify) };
        Assert.Equal("UNPROTECTED_LOCATION", PartitionManager.PrivilegedHelper.Engine.InstallLocationCheck.Evaluate(rules, @"C:\Users\x\AppData\Local\Programs\pm").Code);
    }

    [Fact]
    public void Real_user_profile_folder_is_rejected_without_override()
    {
        // The test output folder lives under the developer's tree, which the current user can write to.
        var rules = new DirectoryInfo(AppContext.BaseDirectory).GetAccessControl()
            .GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier)).Cast<System.Security.AccessControl.FileSystemAccessRule>();
        Assert.False(PartitionManager.PrivilegedHelper.Engine.InstallLocationCheck.Evaluate(rules, AppContext.BaseDirectory).IsValid);
    }
}
