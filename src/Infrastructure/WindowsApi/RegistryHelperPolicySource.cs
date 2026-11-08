using Microsoft.Win32;
using System.Text.Json;
using PartitionManager.Core.Execution;

namespace PartitionManager.Infrastructure.WindowsApi;

public interface IHelperPolicySource
{
    HelperPolicy Load();
}

/// <summary>
/// Loads the write policy from HKLM\SOFTWARE\PartitionManager\Helper, which only administrators can change.
/// Missing, unreadable or unrecognised values mean Disabled.
///   WriteMode         REG_SZ        "Disabled" | "VirtualDisksOnly" | "AllDisks" (installer default from 0.6)
///   AllowedImageRoots REG_MULTI_SZ  folders such as D:\pm-testdisks
///   PhysicalTestDiskIds REG_MULTI_SZ  TEST MACHINES ONLY: MSFT_Disk.UniqueId of physical disks enrolled for in-place shrink/extend
/// </summary>
public sealed class RegistryHelperPolicySource : IHelperPolicySource
{
    public HelperPolicy Load()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HelperPolicy.RegistryKey);
            if (key is null) return HelperPolicy.Disabled;
            var mode = key.GetValue("WriteMode") as string;
            var roots = key.GetValue("AllowedImageRoots") as string[] ?? [];
            return Enum.TryParse<WriteMode>(mode, ignoreCase: false, out var m) && Enum.IsDefined(m)
                ? new HelperPolicy(m, roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray())
                {
                    InitializationApprovals = JsonSerializer.Deserialize<BlankImageApproval[]>(key.GetValue("InitializationApprovals") as string ?? "[]") ?? [],
                    PhysicalTestDiskIds = (key.GetValue("PhysicalTestDiskIds") as string[] ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray(),
                }
                : HelperPolicy.Disabled;
        }
        catch (Exception)
        {
            return HelperPolicy.Disabled;
        }
    }
}


/// <summary>A fixed policy, for demo mode and tests.</summary>
public sealed class FixedHelperPolicySource(HelperPolicy policy) : IHelperPolicySource
{
    public HelperPolicy Load() => policy;
}
