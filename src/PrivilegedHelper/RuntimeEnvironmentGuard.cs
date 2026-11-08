using System.Collections;

namespace PartitionManager.PrivilegedHelper;

/// <summary>
/// Refuses to serve when the elevated helper's environment carries .NET runtime injection or diagnostics settings.
/// A UAC-elevated process receives the user's profile environment (HKCU\Environment), so a medium-integrity process
/// of the same user can set these variables.
///
/// LIMITATION, NOT A FIX: CoreCLR acts on profiler variables (CORECLR_ENABLE_PROFILING / CORECLR_PROFILER_PATH)
/// before Main runs. On the installed 0.5.1-preview helper in the disposable EC2 VM, the runtime mapped a DLL from a
/// user-writable folder named only by these variables. By the time this guard runs, that DLL's DllMain has already
/// executed elevated. The guard only stops the helper from accepting work in such an environment and makes the
/// condition visible. The real fix is a clean launch environment: a service-hosted helper, or a native entry point
/// that clears the environment before the runtime starts. It is tracked as a release blocker.
/// </summary>
internal static class RuntimeEnvironmentGuard
{
    // Variables the runtime reads from DOTNET_* / COMPlus_* (CLRConfig), plus profiler and hosting settings.
    // COREHOST_* is read by the apphost/hostfxr before the runtime starts (trace files written with elevated rights).
    private static readonly string[] RefusedPrefixes = { "DOTNET_", "COMPLUS_", "CORECLR_", "COR_", "COREHOST_" };

    // Read only by the .NET SDK/CLI, which never runs inside the self-contained helper. These are commonly set on
    // developer machines and are not CLRConfig values.
    private static readonly string[] AllowedPrefixes = { "DOTNET_CLI_" };
    private static readonly HashSet<string> AllowedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // DOTNET_ROOT* and DOTNET_HOST_PATH are deliberately NOT allowed: in a framework-dependent build they choose
        // where hostfxr is loaded from.
        "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "DOTNET_GENERATE_ASPNET_CERTIFICATE",
        "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_INSTALL_DIR",
        "DOTNET_WATCH_SUPPRESS_EMOJIS", "DOTNET_UPGRADEASSISTANT_TELEMETRY_OPTOUT",
    };

    /// <summary>Names of refused variables (values are never returned or logged).</summary>
    public static IReadOnlyList<string> RefusedVariables(IDictionary environment)
    {
        var refused = new List<string>();
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is not string name || name.Length == 0) continue;
            if (!RefusedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (AllowedNames.Contains(name) || AllowedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            refused.Add(name);
        }
        refused.Sort(StringComparer.OrdinalIgnoreCase);
        return refused;
    }

    public static IReadOnlyList<string> RefusedVariables() => RefusedVariables(Environment.GetEnvironmentVariables());
}
