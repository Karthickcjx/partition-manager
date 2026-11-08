// Must be a top-level type named StartupHook with "public static void Initialize()" (.NET startup hook contract).
internal static class StartupHook
{
    public static void Initialize()
    {
        var marker = Environment.GetEnvironmentVariable("PM_HOOK_MARKER");
        if (string.IsNullOrEmpty(marker)) return;
        File.WriteAllText(marker, $"startup hook executed in {Environment.ProcessPath} pid {Environment.ProcessId} as {Environment.UserName}");
    }
}
