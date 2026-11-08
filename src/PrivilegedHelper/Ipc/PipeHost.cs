using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PartitionManager.Core.Execution;

namespace PartitionManager.PrivilegedHelper.Ipc;

public static class PipeHost
{
    /// <summary>Pipe names are generated per launch by the client; only this shape is accepted.</summary>
    public static bool IsValidPipeName(string name) =>
        name.StartsWith("PartitionManager.Helper.", StringComparison.Ordinal) && name.Length <= 80
        && name.Skip("PartitionManager.Helper.".Length).All(c => char.IsAsciiHexDigit(c) || c == '-');

    /// <summary>
    /// Creates the single pipe instance. Only the launching user's SID (and Administrators, SYSTEM) may connect, network
    /// logons are denied, and FirstPipeInstance makes creation fail if anyone pre-created the name.
    /// </summary>
    public static NamedPipeServerStream Create(string name, SecurityIdentifier clientUser)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(clientUser, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 64 * 1024, 64 * 1024, security);
    }

    public static int ClientProcessId(NamedPipeServerStream pipe) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) ? (int)pid : throw new Win32Exception();

    public static SecurityIdentifier ProcessUser(int pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid) throw new Win32Exception();
        if (!OpenProcessToken(process, TokenQuery, out var token)) throw new Win32Exception();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User ?? throw new InvalidOperationException("Process has no user SID.");
    }

    public static string ProcessImagePath(int pid)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid) throw new Win32Exception();
        var buffer = new StringBuilder(1024);
        var size = buffer.Capacity;
        if (!QueryFullProcessImageName(process, 0, buffer, ref size)) throw new Win32Exception();
        return buffer.ToString();
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}

/// <summary>
/// Accepts only the exact client that launched the helper: correct one-time token (constant-time compare), the
/// pipe's real client PID equal to the launching PID, that process's image equal to the expected app executable.
/// </summary>
public sealed class LaunchingClientVerifier(byte[] expectedToken, int expectedPid, string expectedImagePath, Func<int> actualClientPid, Func<int, string> imagePathOf)
    : IClientVerifier
{
    public ValidationResult Verify(HelloMessage hello)
    {
        byte[] presented;
        try { presented = Convert.FromHexString(hello.Token ?? ""); }
        catch (FormatException) { return ValidationResult.Fail("BAD_TOKEN", "Authentication failed."); }
        if (presented.Length != expectedToken.Length || !CryptographicOperations.FixedTimeEquals(presented, expectedToken))
            return ValidationResult.Fail("BAD_TOKEN", "Authentication failed.");

        int pid;
        try { pid = actualClientPid(); }
        catch (Exception) { return ValidationResult.Fail("PEER_UNKNOWN", "Could not identify the connecting process."); }
        if (pid != expectedPid || hello.ClientProcessId != expectedPid)
            return ValidationResult.Fail("WRONG_PEER", "The connecting process is not the one that started the helper.");

        string image;
        try { image = imagePathOf(pid); }
        catch (Exception) { return ValidationResult.Fail("PEER_UNKNOWN", "Could not inspect the connecting process."); }
        if (!string.Equals(Path.GetFullPath(image), Path.GetFullPath(expectedImagePath), StringComparison.OrdinalIgnoreCase))
            return ValidationResult.Fail("WRONG_IMAGE", "The connecting program is not Partition Manager.");
        return ValidationResult.Ok;
    }
}
