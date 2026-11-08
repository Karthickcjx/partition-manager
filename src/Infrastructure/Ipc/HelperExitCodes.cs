namespace PartitionManager.Infrastructure.Ipc;

/// <summary>
/// Exit codes of PartitionManager.Helper.exe (mirrors PartitionManager.PrivilegedHelper.Program, enforced by a test),
/// translated into messages the app can show when the helper stops before accepting the connection.
/// </summary>
public static class HelperExitCodes
{
    public const int BadArguments = 64, NotElevated = 65, ParentGone = 66, PipeFailed = 67, UnprotectedLocation = 68, UnsafeEnvironment = 69, HelperBusy = 70;

    public static string Describe(int exitCode) => exitCode switch
    {
        BadArguments => "The privileged helper rejected its start-up arguments. Nothing was changed.",
        NotElevated => "The privileged helper did not receive administrator rights. Nothing was changed.",
        ParentGone => "The privileged helper could not verify this app's process. Nothing was changed.",
        PipeFailed => "The privileged helper could not create its private connection (another program may be using its name). Nothing was changed.",
        UnprotectedLocation => "The privileged helper refused to run because its installation or ProgramData\\PartitionManager folder is not protected. Reinstall Partition Manager to repair the folder permissions. Nothing was changed.",
        UnsafeEnvironment => "The privileged helper refused to run because .NET runtime or profiler variables (DOTNET_*, COMPlus_*, CORECLR_*, COR_*) are set in your user environment. Remove them, sign out and in again, and retry. Nothing was changed.",
        HelperBusy => "Another privileged helper is still running an operation (for example a Move started before the app was closed). Wait for it to finish, then refresh. Nothing was changed.",
        _ => $"The privileged helper stopped unexpectedly (exit code {exitCode}). Nothing was changed.",
    };
}
