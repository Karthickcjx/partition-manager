using System.Text.Json;
using Xunit;

namespace PartitionManager.Tests.Production;

/// <summary>
/// Regression guards for defects found on the installed 0.5.0-preview in the disposable EC2 VM:
///  * the elevated helper executed a DOTNET_STARTUP_HOOKS assembly from its environment (privilege escalation);
///  * a standard user could pre-create the helper's ProgramData folder before first use (fail-closed DoS);
///  * the helper was published with native-library self-extraction (temp-directory DLL loading risk).
/// </summary>
public class RuntimeHardeningTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PartitionManager.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Configuration() =>
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}") ? "Release" : "Debug";

    private static JsonElement RuntimeConfigProperties(string project, string assembly)
    {
        var path = Path.Combine(RepoRoot(), "src", project, "bin", Configuration(), "net8.0-windows", "win-x64", assembly + ".runtimeconfig.json");
        Assert.True(File.Exists(path), "missing " + path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties").Clone();
    }

    /// <summary>The helper is referenced by the tests, so its generated runtime configuration is always current.</summary>
    [Fact]
    public void Startup_hooks_are_disabled_in_the_helper_runtime_configuration()
    {
        var props = RuntimeConfigProperties("PrivilegedHelper", "PartitionManager.Helper");
        Assert.True(props.TryGetProperty("System.StartupHookProvider.IsSupported", out var v), "StartupHookSupport not configured");
        Assert.False(v.GetBoolean());
    }

    /// <summary>The app is not referenced by the tests (its bin output may be stale), so check the project setting.</summary>
    [Fact]
    public void Startup_hooks_are_disabled_for_the_app()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Desktop", "PartitionManager.Desktop.csproj"));
        Assert.Contains("<StartupHookSupport>false</StartupHookSupport>", csproj);
    }

    [Fact]
    public void Installer_secures_the_data_folder_before_copying_files()
    {
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "PartitionManager.iss"));
        Assert.Contains("if CurStep = ssInstall then", iss);
        Assert.Contains("/setowner *S-1-5-32-544 /T /C /Q", iss);
        Assert.Contains("/inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX", iss);
        Assert.Contains("RaiseException", iss);
        Assert.Contains("PrivilegesRequired=admin", iss);
    }

    [Theory]
    [InlineData("CORECLR_ENABLE_PROFILING")]
    [InlineData("CORECLR_PROFILER_PATH_64")]
    [InlineData("cor_enable_profiling")]
    [InlineData("COR_PROFILER_PATH")]
    [InlineData("DOTNET_STARTUP_HOOKS")]
    [InlineData("DOTNET_DbgEnableMiniDump")]
    [InlineData("DOTNET_DbgMiniDumpName")]
    [InlineData("DOTNET_EnableDiagnostics")]
    [InlineData("COMPlus_EnableDiagnostics")]
    [InlineData("complus_ZapDisable")]
    [InlineData("COREHOST_TRACE")]
    [InlineData("COREHOST_TRACEFILE")]
    [InlineData("DOTNET_ROOT")]
    [InlineData("DOTNET_ROOT_X64")]
    [InlineData("DOTNET_HOST_PATH")]
    public void Helper_refuses_runtime_injection_and_diagnostics_variables(string name)
    {
        var env = new System.Collections.Hashtable { [name] = "x", ["PATH"] = @"C:\Windows" };
        Assert.Equal(new[] { name }, PartitionManager.PrivilegedHelper.RuntimeEnvironmentGuard.RefusedVariables(env));
    }

    [Theory]
    [InlineData("DOTNET_CLI_TELEMETRY_OPTOUT")]
    [InlineData("dotnet_nologo")]
    [InlineData("CORRELATION_ID")]
    [InlineData("PATH")]
    public void Helper_accepts_sdk_only_and_unrelated_variables(string name)
    {
        var env = new System.Collections.Hashtable { [name] = "1" };
        Assert.Empty(PartitionManager.PrivilegedHelper.RuntimeEnvironmentGuard.RefusedVariables(env));
    }

    [Fact]
    public void Refused_variable_values_are_never_reported()
    {
        var env = new System.Collections.Hashtable { ["CORECLR_PROFILER_PATH"] = @"C:\Users\Public\secret-path.dll" };
        var refused = PartitionManager.PrivilegedHelper.RuntimeEnvironmentGuard.RefusedVariables(env);
        Assert.DoesNotContain(refused, r => r.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Helper_is_never_published_with_native_self_extraction()
    {
        var csproj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "PrivilegedHelper", "PartitionManager.PrivilegedHelper.csproj"));
        Assert.Contains("<IncludeNativeLibrariesForSelfExtract>false</IncludeNativeLibrariesForSelfExtract>", csproj);
        Assert.Contains("<StartupHookSupport>false</StartupHookSupport>", csproj);
        var build = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "build-release.ps1"));
        Assert.Contains("@helperPublish", build);
        Assert.Contains("'-p:IncludeNativeLibrariesForSelfExtract=false'", build);
    }
}
