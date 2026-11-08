using System.Diagnostics;
using System.IO.Pipes;
using PartitionManager.Infrastructure.Ipc;
using Xunit;

namespace PartitionManager.Tests.Production;

/// <summary>
/// Visible error handling when the elevated helper refuses to start. Before 0.5.1, a helper that exited at once
/// (for example exit 68 for a squatted ProgramData folder) left the app waiting 30 s and then reporting only
/// "Could not connect".
/// </summary>
public class HelperStartupFailureTests
{
    [Fact]
    public void Exit_codes_match_the_helper()
    {
        Assert.Equal(PartitionManager.PrivilegedHelper.Program.ExitBadArgs, HelperExitCodes.BadArguments);
        Assert.Equal(PartitionManager.PrivilegedHelper.Program.ExitNotElevated, HelperExitCodes.NotElevated);
        Assert.Equal(PartitionManager.PrivilegedHelper.Program.ExitParentGone, HelperExitCodes.ParentGone);
        Assert.Equal(PartitionManager.PrivilegedHelper.Program.ExitPipeFailed, HelperExitCodes.PipeFailed);
        Assert.Equal(PartitionManager.PrivilegedHelper.Program.ExitUnprotectedLocation, HelperExitCodes.UnprotectedLocation);
        Assert.Equal(PartitionManager.PrivilegedHelper.Program.ExitUnsafeEnvironment, HelperExitCodes.UnsafeEnvironment);
        Assert.Equal(PartitionManager.PrivilegedHelper.Program.ExitHelperBusy, HelperExitCodes.HelperBusy);
    }

    [Fact]
    public void Every_known_exit_code_has_a_distinct_message_that_says_nothing_changed()
    {
        var codes = new[] { 64, 65, 66, 67, 68, 69, 70 };
        var messages = codes.Select(HelperExitCodes.Describe).ToList();
        Assert.Equal(codes.Length, messages.Distinct().Count());
        Assert.All(messages, m => Assert.Contains("Nothing was changed.", m));
        Assert.Contains("exit code 3", HelperExitCodes.Describe(3));
        Assert.Contains("Reinstall", HelperExitCodes.Describe(68));
        Assert.Contains("CORECLR_", HelperExitCodes.Describe(69));
    }

    [Fact]
    public async Task A_helper_that_exits_is_reported_with_its_reason_without_waiting_for_the_timeout()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 69") { UseShellExecute = false, CreateNoWindow = true })!;
        await using var pipe = new NamedPipeClientStream(".", "PartitionManager.Test." + Guid.NewGuid().ToString("N"), PipeDirection.InOut, PipeOptions.Asynchronous);
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<HelperUnavailableException>(() => HelperClient.ConnectWhileRunningAsync(pipe, process, TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.Equal(HelperExitCodes.Describe(69), ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }

    [Fact]
    public async Task A_running_helper_that_never_listens_still_times_out()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            await using var pipe = new NamedPipeClientStream(".", "PartitionManager.Test." + Guid.NewGuid().ToString("N"), PipeDirection.InOut, PipeOptions.Asynchronous);
            await Assert.ThrowsAsync<TimeoutException>(() => HelperClient.ConnectWhileRunningAsync(pipe, process, TimeSpan.FromSeconds(1.5), CancellationToken.None));
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }
}
