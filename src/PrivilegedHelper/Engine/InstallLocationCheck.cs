using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using PartitionManager.Core.Execution;

namespace PartitionManager.PrivilegedHelper.Engine;

/// <summary>
/// An elevated helper living in a folder that standard users can write to is a privilege-escalation hole: anyone
/// could replace the helper (or the client exe it trusts) and get their code elevated through the UAC prompt.
/// The helper therefore refuses to run unless its folder is writable only by Administrators, SYSTEM and
/// TrustedInstaller. Development registry overrides are deliberately ignored.
/// </summary>
public static class InstallLocationCheck
{
    private static readonly SecurityIdentifier[] Trusted =
    [
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.LocalSystemSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // NT SERVICE\TrustedInstaller
    ];

    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    public static ValidationResult Check(string directory)
    {
        try
        {
            var dir = new DirectoryInfo(directory);
            foreach (FileSystemInfo item in new FileSystemInfo[] { dir }.Concat(dir.EnumerateFiles()))
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                    return ValidationResult.Fail("UNPROTECTED_LOCATION", "Reparse points are not allowed in the helper installation.");
                FileSystemSecurity acl = item is DirectoryInfo d ? d.GetAccessControl() : ((FileInfo)item).GetAccessControl();
                if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted.Contains(owner))
                    return ValidationResult.Fail("UNPROTECTED_LOCATION", "The helper directory or a dependency has an untrusted owner.");
                var result = Evaluate(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), item.FullName);
                if (!result.IsValid) return result;
            }
            for (var parent = dir.Parent; parent is not null; parent = parent.Parent)
            {
                if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                    return ValidationResult.Fail("UNPROTECTED_LOCATION", "An installation ancestor is a reparse point.");
                foreach (var rule in parent.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>())
                    if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                        (rule.FileSystemRights & (FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0 &&
                        rule.IdentityReference is SecurityIdentifier sid && !Trusted.Contains(sid))
                        return ValidationResult.Fail("UNPROTECTED_LOCATION", "An untrusted principal can replace an installation ancestor.");
            }
            return ValidationResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ValidationResult.Fail("UNPROTECTED_LOCATION", "Cannot verify helper installation: " + ex.Message);
        }
    }

    internal static ValidationResult Evaluate(IEnumerable<FileSystemAccessRule> rules, string directory)
    {
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 || (rule.FileSystemRights & WriteRights) == 0) continue;
            if (rule.IdentityReference is SecurityIdentifier sid && Trusted.Contains(sid)) continue;
            return ValidationResult.Fail("UNPROTECTED_LOCATION",
                $"The helper is in a folder that {rule.IdentityReference} can modify ({directory}). Install it under Program Files.");
        }
        return ValidationResult.Ok;
    }

}
