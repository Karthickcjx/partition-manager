using System.Security.Principal;
using PartitionManager.Core.Execution;
using PartitionManager.Core.Logging;
using PartitionManager.Infrastructure.Logging;
using PartitionManager.Infrastructure.WindowsApi;
using PartitionManager.PrivilegedHelper.Engine;
using PartitionManager.PrivilegedHelper.Ipc;

namespace PartitionManager.PrivilegedHelper;

/// <summary>
/// Elevated helper. Started on demand by the app via UAC with
///   --pipe PartitionManager.Helper.&lt;guid&gt; --parent &lt;pid&gt; --token &lt;64 hex chars&gt;
/// serves exactly one authenticated client over one pipe instance, then exits. It installs nothing persistent:
/// no service, no scheduled task, no autostart entry.
/// </summary>
public static class Program
{
    public const int ExitOk = 0, ExitBadArgs = 64, ExitNotElevated = 65, ExitParentGone = 66, ExitPipeFailed = 67, ExitUnprotectedLocation = 68,
        ExitUnsafeEnvironment = 69, ExitHelperBusy = 70;

    public static async Task<int> Main(string[] args)
    {
        // Elevated code must not append to user-controlled LOCALAPPDATA paths.
        // Operation records go to the validated ProgramData audit sink below.
        var log = NullOperationLog.Instance;
        if (!Elevation.IsElevated)
        {
            log.Error("helper", "refusing to run without administrator rights");
            return ExitNotElevated;
        }
        // Defence in depth only (see RuntimeEnvironmentGuard): profiler variables have already been acted on by now.
        var unsafeEnvironment = RuntimeEnvironmentGuard.RefusedVariables();
        if (unsafeEnvironment.Count > 0)
        {
            log.Error("helper", "refusing to run with .NET runtime variables in the environment: " + string.Join(", ", unsafeEnvironment));
            return ExitUnsafeEnvironment;
        }
        if (!TryParse(args, out var pipeName, out var parentPid, out var token))
        {
            log.Error("helper", "bad arguments");
            return ExitBadArgs;
        }

        SecurityIdentifier clientUser;
        string clientImage;
        try
        {
            clientUser = PipeHost.ProcessUser(parentPid);
            clientImage = PipeHost.ProcessImagePath(parentPid);
        }
        catch (Exception ex)
        {
            log.Error("helper", "parent process not available: " + ex.Message);
            return ExitParentGone;
        }

        var location = InstallLocationCheck.Check(AppContext.BaseDirectory);
        if (!location.IsValid)
        {
            log.Error("helper", location.Message);
            return ExitUnprotectedLocation;
        }

        // The client must be the PartitionManager.exe that sits next to this helper; nothing else may drive it.
        var expectedImage = Path.Combine(AppContext.BaseDirectory, "PartitionManager.exe");
        var policySource = new RegistryHelperPolicySource();
        FileAuditSink audit;
        try { audit = new FileAuditSink(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error("helper", "unsafe audit location: " + ex.Message);
            return ExitUnprotectedLocation;
        }
        // Fail closed at start-up if the audit/journal folder is not protected, but take the exclusive journal lease only
        // when a write begins: a read-only (Data Recovery) helper must not block a second helper doing partition work.
        var auditCheck = InstallLocationCheck.Check(audit.AuditDirectory);
        if (!auditCheck.IsValid) { log.Error("helper", "unsafe journal location: " + auditCheck.Message); return ExitUnprotectedLocation; }
        var journal = new LazyOperationJournal(() => new FileOperationJournal(audit.AuditDirectory, directory =>
        {
            var result = InstallLocationCheck.Check(directory);
            if (!result.IsValid) throw new UnauthorizedAccessException(result.Message);
        }));
        using var journalLifetime = journal;
        var discovery = new WmiDiskDiscoveryService(log);
        var engine = new WriteEngine(discovery, new WmiStorageWriteBackend(), policySource, audit) { Journal = journal };
        var recovery = new RecoveryService(discovery, audit);
        var mode = policySource.Load().Mode.ToString();
        log.Info("helper", $"started for pid {parentPid} ({clientImage}); write mode {mode}");

        try
        {
            await using var pipe = PipeHost.Create(pipeName, clientUser);
            // Long enough for a block-level Move of a large partition; the client connects within 60 s or the helper exits.
            using var cts = new CancellationTokenSource(TimeSpan.FromHours(12));
            var connect = pipe.WaitForConnectionAsync(cts.Token);
            if (await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(60), cts.Token)) != connect)
            {
                log.Warn("helper", "no client connected within 60 s");
                return ExitOk;
            }
            await connect;
            var verifier = new LaunchingClientVerifier(token, parentPid, expectedImage, () => PipeHost.ClientProcessId(pipe), PipeHost.ProcessImagePath);
            await new HelperSession(pipe, verifier, engine, isElevated: true, mode, recovery: recovery).RunAsync(cts.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            log.Error("helper", "pipe error: " + ex.Message);
            return ExitPipeFailed;
        }
        log.Info("helper", "session ended");
        return ExitOk;
    }

    internal static bool TryParse(string[] args, out string pipeName, out int parentPid, out byte[] token)
    {
        pipeName = ""; parentPid = 0; token = [];
        if (args.Length != 6) return false;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !map.TryAdd(args[i], args[i + 1])) return false;
        }
        if (!map.TryGetValue("--pipe", out var pipe) || !PipeHost.IsValidPipeName(pipe)) return false;
        if (!map.TryGetValue("--parent", out var pidText) || !int.TryParse(pidText, out var pid) || pid <= 0) return false;
        if (!map.TryGetValue("--token", out var tokenText) || tokenText.Length != 64) return false;
        try { token = Convert.FromHexString(tokenText); }
        catch (FormatException) { return false; }
        pipeName = pipe;
        parentPid = pid;
        return true;
    }
}
