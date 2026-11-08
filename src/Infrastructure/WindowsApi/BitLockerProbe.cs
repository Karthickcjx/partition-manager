using System.Management;
using PartitionManager.Core.Models;

namespace PartitionManager.Infrastructure.WindowsApi;

public interface IBitLockerProbe
{
    /// <summary>BitLocker state keyed by drive letter. Letters missing from the result are unknown.</summary>
    Dictionary<char, BitLockerState> Probe();
}

/// <summary>
/// Reads Win32_EncryptableVolume (root\cimv2\Security\MicrosoftVolumeEncryption). Windows only exposes this class to
/// administrators; without elevation the probe returns an empty map and callers treat the state as unknown.
/// </summary>
public sealed class WmiBitLockerProbe : IBitLockerProbe
{
    public Dictionary<char, BitLockerState> Probe()
    {
        var result = new Dictionary<char, BitLockerState>();
        if (!Elevation.IsElevated) return result;

        var scope = new ManagementScope(@"root\cimv2\Security\MicrosoftVolumeEncryption");
        scope.Connect();
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DriveLetter, ProtectionStatus, ConversionStatus FROM Win32_EncryptableVolume"));
        using var volumes = searcher.Get();
        foreach (ManagementObject v in volumes)
        {
            var letter = v["DriveLetter"] as string;
            if (string.IsNullOrEmpty(letter)) continue;
            var conversion = Convert.ToUInt32(v["ConversionStatus"] ?? 0u);

            // GetLockStatus: 0 = unlocked, 1 = locked.
            var locked = false;
            try
            {
                var outParams = v.InvokeMethod("GetLockStatus", null, null);
                locked = Convert.ToUInt32(outParams?["LockStatus"] ?? 0u) == 1;
            }
            catch (ManagementException) { }

            // ConversionStatus 0 = fully decrypted; anything else means some or all of the volume is encrypted.
            result[char.ToUpperInvariant(letter[0])] = locked ? BitLockerState.Locked
                : conversion == 0 ? BitLockerState.NotEncrypted : BitLockerState.Encrypted;
        }
        return result;
    }
}
