using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PartitionManager.Core.Execution;

namespace PartitionManager.PrivilegedHelper.Engine;

/// <summary>
/// Append-only JSON-lines audit trail in %ProgramData%\PartitionManager\Audit. The folder is created with an ACL
/// that lets only Administrators and SYSTEM write, so unprivileged processes cannot forge or delete entries.
/// </summary>
public sealed class FileAuditSink : IAuditSink
{
    private readonly string _dir;
    private readonly object _lock = new();

    public FileAuditSink(string? directory = null)
    {
        _dir = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PartitionManager", "Audit");
        // Never take ownership of an untrusted pre-created path: it could redirect
        // privileged writes. Create with a protected ACL, or refuse existing paths.
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.SetOwner(admins);
        security.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        var info = new DirectoryInfo(_dir);
        var parent = info.Parent ?? throw new IOException("Audit directory has no parent.");
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Audit path has a reparse ancestor.");
        if (!parent.Exists) parent.Create(security);
        var parentCheck = InstallLocationCheck.Check(parent.FullName);
        if (!parentCheck.IsValid) throw new UnauthorizedAccessException(parentCheck.Message);
        if (!info.Exists) info.Create(security);
        var check = InstallLocationCheck.Check(info.FullName);
        if (!check.IsValid) throw new UnauthorizedAccessException(check.Message);
    }

    public string AuditDirectory => _dir;

    public void Record(string requestId, string kind, string target, string outcome, string detail)
    {
        var line = JsonSerializer.Serialize(new
        {
            time = DateTimeOffset.UtcNow,
            user = Environment.UserName,
            requestId,
            kind,
            target,
            outcome,
            detail,
        });
        lock (_lock)
        {
            var check = InstallLocationCheck.Check(_dir);
            if (!check.IsValid) throw new UnauthorizedAccessException(check.Message);
            File.AppendAllText(Path.Combine(_dir, $"audit-{DateTime.UtcNow:yyyyMMdd}.jsonl"), line + Environment.NewLine);
        }
    }
}
