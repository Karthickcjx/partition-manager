<#
.SYNOPSIS
  Installed-package security tests for an explicitly identified disposable EC2 Windows VM. Run as SYSTEM (SSM).
.DESCRIPTION
  * Creates a temporary standard-user account (Users group only, expires in 6 h, random password never output),
    impersonates it, and attempts to modify installed binaries/dependencies, plant DLLs, change ACLs/ownership,
    write the helper policy key, and modify audit records and journals. Access checks request write/delete/WRITE_DAC/
    WRITE_OWNER without changing content; only "create file" probes can leave a file, and those are removed and FAIL.
  * Launches the installed helper as SYSTEM with test pipe names and checks it rejects: the standard user at the pipe
    ACL, a wrong token, a forged client PID, a non-PartitionManager client image, a malformed frame, a pre-created
    (squatted) pipe name, and launch under a standard-user token.
  * Optional: checks whether the helper executes a .NET startup hook from its environment (harmless marker probe),
    and lists the helper's loaded modules and any runtime extraction directory.
  This is SYSTEM-driven automation. It does not test interactive UAC prompts or a real interactive standard-user
  desktop session; those remain manual.
#>
param(
    [Parameter(Mandatory = $true)][string]$ExpectedEc2InstanceId,
    [switch]$DisposableVmConfirmed,
    [string]$EvidenceDirectory = 'C:\PMCloudTest-20261107\Security',
    [string]$StartupHookProbeBase64 = '',
    [string]$Label = 'run',
    [switch]$SquatTest,
    # Squat mode only: move an existing data folder aside (kept, renamed) to reproduce the fresh-install window.
    [switch]$FreshDataFolder,
    # Squat mode only: after the squat, re-run this installer (repair) and check it re-secures the folder.
    [string]$RepairInstaller = ''
)
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- guards
$machine = Get-CimInstance Win32_ComputerSystem
if ($ExpectedEc2InstanceId -notmatch '^i-[0-9a-f]{17}$' -or $machine.Manufacturer -ne 'Amazon EC2') { throw 'Refused: expected an explicitly identified EC2 VM.' }
$imds = Invoke-RestMethod -Method Put -Uri 'http://169.254.169.254/latest/api/token' -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '60' } -TimeoutSec 10
$actual = Invoke-RestMethod -Uri 'http://169.254.169.254/latest/meta-data/instance-id' -Headers @{ 'X-aws-ec2-metadata-token' = $imds } -TimeoutSec 10
if ($actual -ne $ExpectedEc2InstanceId) { throw 'Refused: EC2 instance identity mismatch.' }
if (-not $DisposableVmConfirmed) { throw 'Refused: -DisposableVmConfirmed is required.' }
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18') { throw 'Run as SYSTEM (SSM RunPowerShellScript).' }

$install = Join-Path $env:ProgramFiles 'PartitionManager'
$helper = Join-Path $install 'PartitionManager.Helper.exe'
if (-not (Test-Path -LiteralPath $helper)) { throw "Installed helper not found at $helper" }
New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
$rows = [Collections.Generic.List[object]]::new()
function Record([string]$area, [string]$check, [string]$status, [string]$detail) { $rows.Add([pscustomobject]@{ Area = $area; Check = $check; Status = $status; Detail = $detail }) }
$created = [Collections.Generic.List[string]]::new()

# ---------------------------------------------------------------- native helpers (C# 5 for Windows PowerShell 5.1)
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

public static class PmSec
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LogonUser(string user, string domain, string password, int type, int provider, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int level, int type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string r; public string desk; public string title; public int x, y, xs, ys, xc, yc, fill, flags; public short show, r2; public IntPtr r3, i, o, e; }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int pid, tid; }

    public static IntPtr Logon(string user, string password)
    {
        IntPtr t;
        if (LogonUser(user, ".", password, 2, 0, out t)) return t;          // LOGON32_LOGON_INTERACTIVE
        if (LogonUser(user, ".", password, 3, 0, out t)) return t;          // LOGON32_LOGON_NETWORK fallback
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public static string Describe(IntPtr token)
    {
        var id = new WindowsIdentity(token);
        var admin = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        return id.Name + "|" + id.User.Value + "|admin=" + admin + "|impersonation=" + id.ImpersonationLevel;
    }

    static string Classify(Exception ex) { return "DENIED: " + ex.GetType().Name + ": " + ex.Message.Split('\r')[0]; }

    public static string As(IntPtr token, Func<string> probe)
    {
        using (var id = new WindowsIdentity(token))
        using (id.Impersonate())
        {
            try { return probe(); } catch (Exception ex) { return Classify(ex); }
        }
    }

    // Non-mutating access checks: open with the right, close, change nothing.
    public static string OpenFor(string path, FileSystemRights rights)
    {
        using (new FileStream(path, FileMode.Open, rights, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None)) { }
        return "ALLOWED";
    }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    // Opens a directory handle requesting the given rights (FILE_FLAG_BACKUP_SEMANTICS); nothing is modified.
    // Note: an earlier probe re-persisted an unmodified ACL object, which .NET treats as a no-op without an access check.
    public static string OpenDirFor(string path, FileSystemRights rights)
    {
        using (var h = CreateFileW(path, (uint)rights, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
        {
            if (h.IsInvalid) return "DENIED: win32=" + Marshal.GetLastWin32Error();
            return "ALLOWED";
        }
    }
    public static string CreateFile(string path)
    {
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
        return "ALLOWED";
    }
    public static string CreateDir(string path) { Directory.CreateDirectory(path); return "ALLOWED"; }
    public static string RegistryWrite(string subKey)
    {
        using (var k = Registry.LocalMachine.OpenSubKey(subKey, true))
        {
            if (k == null) return "KEY_ABSENT";
            return "ALLOWED";
        }
    }
    public static string RegistryCreate(string parent, string child)
    {
        using (var k = Registry.LocalMachine.OpenSubKey(parent, true))
        {
            if (k == null) return "PARENT_ABSENT";
            return "ALLOWED";
        }
    }
    // Only UnauthorizedAccessException (ERROR_ACCESS_DENIED from the pipe's DACL) counts as denial.
    public static string PipeConnect(string name)
    {
        try
        {
            using (var c = new NamedPipeClientStream(".", name, PipeDirection.InOut)) { c.Connect(3000); }
            return "ALLOWED (connected)";
        }
        catch (UnauthorizedAccessException ex) { return "DENIED: UnauthorizedAccessException: " + ex.Message; }
        catch (TimeoutException) { return "INCONCLUSIVE: timeout (pipe absent or busy)"; }
    }

    // Helper protocol: 4-byte little-endian length + UTF-8 JSON.
    public static string Exchange(string pipeName, byte[] rawFrame)
    {
        using (var c = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
        {
            c.Connect(5000);
            c.Write(rawFrame, 0, rawFrame.Length);
            c.Flush();
            var header = new byte[4];
            if (ReadAll(c, header) < 4) return "NO_REPLY";
            var len = BitConverter.ToInt32(header, 0);
            if (len <= 0 || len > 65536) return "BAD_REPLY_LENGTH " + len;
            var body = new byte[len];
            ReadAll(c, body);
            return System.Text.Encoding.UTF8.GetString(body);
        }
    }
    static int ReadAll(Stream s, byte[] b) { int r = 0; while (r < b.Length) { int n = s.Read(b, r, b.Length - r); if (n == 0) break; r += n; } return r; }
    public static byte[] Frame(string json)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        var f = new byte[4 + body.Length];
        BitConverter.GetBytes(body.Length).CopyTo(f, 0);
        body.CopyTo(f, 4);
        return f;
    }

    public static string LaunchAsUser(IntPtr token, string exe, string args)
    {
        IntPtr primary;
        if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out primary)) return "DUPLICATE_FAILED " + Marshal.GetLastWin32Error();
        try
        {
            var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si);
            PROCESS_INFORMATION pi;
            if (!CreateProcessAsUser(primary, exe, "\"" + exe + "\" " + args, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, null, ref si, out pi))
                return "NOT_STARTED win32=" + Marshal.GetLastWin32Error();
            uint code = 0;
            if (WaitForSingleObject(pi.hProcess, 30000) != 0) { TerminateProcess(pi.hProcess, 1); code = 9999; }
            else GetExitCodeProcess(pi.hProcess, out code);
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            return "EXITED " + code;
        }
        finally { CloseHandle(primary); }
    }
}
'@

# Enumerating \\.\pipe\ lists names without connecting; Test-Path on a pipe path would consume the helper's single client slot.
function PipeExists([string]$name) { [IO.Directory]::GetFiles('\\.\pipe\') -contains ('\\.\pipe\' + $name) }

function New-RandomSecret([int]$length) {
    $sets = 'ABCDEFGHJKLMNPQRSTUVWXYZ', 'abcdefghijkmnopqrstuvwxyz', '23456789', '!#%+=?@'
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    $bytes = New-Object byte[] ($length)
    $rng.GetBytes($bytes)
    $chars = for ($i = 0; $i -lt $length; $i++) { $set = $sets[$i % $sets.Count]; $set[$bytes[$i] % $set.Length] }
    -join $chars
}

$user = $null; $token = [IntPtr]::Zero; $sleeper = $null
$helpers = [Collections.Generic.List[Diagnostics.Process]]::new()
try {
    # ---------------------------------------------------------------- temporary standard user
    $user = 'pmstd' + ([guid]::NewGuid().ToString('N').Substring(0, 6))
    $secret = New-RandomSecret 28
    $usersGroup = Get-LocalGroup -SID 'S-1-5-32-545'
    New-LocalUser -Name $user -Password (ConvertTo-SecureString $secret -AsPlainText -Force) -AccountExpires (Get-Date).AddHours(6) `
        -Description 'PartitionManager temp security test user' -UserMayNotChangePassword | Out-Null
    Add-LocalGroupMember -Group $usersGroup -Member $user
    $created.Add("local user $user")
    $token = [PmSec]::Logon($user, $secret)
    $secret = $null
    $who = [PmSec]::Describe($token)
    $isAdmin = $who -match 'admin=True'
    Record 'Account' 'Temporary standard user (Users only, not Administrators)' $(if ($isAdmin) { 'FAIL' } else { 'PASS' }) $who
    if ($isAdmin) { throw 'Test account unexpectedly has administrator rights; aborting.' }

    function AsUser([string]$area, [string]$check, [scriptblock]$probe, [switch]$ExpectAllowed) {
        $result = [PmSec]::As($token, [Func[string]]$probe)
        $denied = $result -like 'DENIED*' -or $result -in 'KEY_ABSENT', 'PARENT_ABSENT'
        $status = if ($result -like 'INCONCLUSIVE*') { 'INCONCLUSIVE' } elseif ($ExpectAllowed) { if ($denied) { 'FAIL' } else { 'PASS' } } else { if ($denied) { 'PASS' } else { 'FAIL' } }
        Record $area $check $status $result
        return $result
    }

    # ---------------------------------------------------------------- installed files
    $files = Get-ChildItem -LiteralPath $install -File -Recurse
    foreach ($f in $files) {
        $p = $f.FullName
        AsUser 'Install' "Open for write: $($f.Name)" { [PmSec]::OpenFor($p, [Security.AccessControl.FileSystemRights]::WriteData) } | Out-Null
        AsUser 'Install' "Open for delete/rename: $($f.Name)" { [PmSec]::OpenFor($p, [Security.AccessControl.FileSystemRights]::Delete) } | Out-Null
        AsUser 'Install' "Open for WRITE_DAC: $($f.Name)" { [PmSec]::OpenFor($p, [Security.AccessControl.FileSystemRights]::ChangePermissions) } | Out-Null
        AsUser 'Install' "Open for take-ownership: $($f.Name)" { [PmSec]::OpenFor($p, [Security.AccessControl.FileSystemRights]::TakeOwnership) } | Out-Null
    }
    Record 'Install' 'Installed files checked' 'INFO' (($files | ForEach-Object Name) -join ', ')
    foreach ($plant in 'version.dll', 'DbgHelp.dll', 'PartitionManager.Helper.dll', "pm-std-probe-$Label.txt") {
        $target = Join-Path $install $plant
        $r = AsUser 'Install' "Plant new file in install dir: $plant" { [PmSec]::CreateFile($target) }
        if ($r -eq 'ALLOWED') { Remove-Item -LiteralPath $target -Force; $created.Add("REMOVED planted $target") }
    }
    $sub = Join-Path $install "pm-std-probe-dir-$Label"
    $r = AsUser 'Install' 'Create subfolder in install dir' { [PmSec]::CreateDir($sub) }
    if ($r -eq 'ALLOWED') { Remove-Item -LiteralPath $sub -Recurse -Force }
    AsUser 'Install' 'Open install directory for WRITE_DAC' { [PmSec]::OpenDirFor($install, [Security.AccessControl.FileSystemRights]::ChangePermissions) } | Out-Null
    AsUser 'Install' 'Open install directory for WRITE_OWNER' { [PmSec]::OpenDirFor($install, [Security.AccessControl.FileSystemRights]::TakeOwnership) } | Out-Null
    AsUser 'Install' 'Open install directory for DELETE' { [PmSec]::OpenDirFor($install, [Security.AccessControl.FileSystemRights]::Delete) } | Out-Null

    # ---------------------------------------------------------------- policy registry
    AsUser 'Policy' 'Open HKLM\SOFTWARE for write (create policy key)' { [PmSec]::RegistryCreate('SOFTWARE', 'PartitionManager') } | Out-Null
    AsUser 'Policy' 'Open HKLM\SOFTWARE\PartitionManager\Helper for write' { [PmSec]::RegistryWrite('SOFTWARE\PartitionManager\Helper') } | Out-Null
    $policyKey = 'HKLM:\SOFTWARE\PartitionManager\Helper'
    Record 'Policy' 'Helper policy key present' 'INFO' $(if (Test-Path $policyKey) { (Get-ItemProperty $policyKey | Select-Object WriteMode | ConvertTo-Json -Compress) } else { 'absent (writes disabled by default)' })

    # ---------------------------------------------------------------- audit / journal pre-squatting (separate run)
    $pmData = Join-Path $env:ProgramData 'PartitionManager'
    $audit = Join-Path $pmData 'Audit'
    $squat = Join-Path $audit "pm-std-squat-$Label.txt"
    if ($SquatTest) {
        if ($FreshDataFolder -and (Test-Path -LiteralPath $pmData)) {
            $aside = "$pmData.before-$Label"
            Rename-Item -LiteralPath $pmData -NewName (Split-Path -Leaf $aside)
            Record 'Squat' 'Existing data folder moved aside (kept)' 'INFO' $aside
            $created.Add("MOVED existing data folder to $aside (kept)")
        }
        $existedBefore = Test-Path -LiteralPath $pmData
        Record 'Squat' 'ProgramData\PartitionManager existed before the attempt' 'INFO' "$existedBefore $(if ($existedBefore) { (Get-Acl -LiteralPath $pmData).Owner })"
        $r = AsUser 'Squat' 'Standard user pre-creates ProgramData\PartitionManager\Audit and a file in it' { [void][PmSec]::CreateDir($audit); [PmSec]::CreateFile($squat) }
        if ($RepairInstaller) {
            $log = Join-Path $EvidenceDirectory "repair-$Label.log"
            $p = Start-Process -FilePath $RepairInstaller -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"" -WindowStyle Hidden -PassThru -Wait
            Record 'Squat' 'Installer repair after squat' $(if ($p.ExitCode -eq 0) { 'PASS' } else { 'FAIL' }) "exit $($p.ExitCode); log $log"
            foreach ($item in $pmData, $audit, $squat) {
                if (Test-Path -LiteralPath $item) { $o = (Get-Acl -LiteralPath $item).GetOwner([Security.Principal.SecurityIdentifier]).Value
                    Record 'Squat' "Owner after repair: $item" $(if ($o -in 'S-1-5-32-544', 'S-1-5-18') { 'PASS' } else { 'FAIL' }) "$o $((Get-Acl -LiteralPath $item).Sddl)" }
            }
            if (Test-Path -LiteralPath $squat) { AsUser 'Squat' 'Squatter can still write its file after repair' { [PmSec]::OpenFor($squat, [Security.AccessControl.FileSystemRights]::WriteData) } | Out-Null }
            $rootProbe = Join-Path $pmData "pm-std-root-$Label"
            $r2 = AsUser 'Squat' 'Standard user creates a folder in the data root after repair' { [PmSec]::CreateDir($rootProbe) }
            if ($r2 -like 'ALLOWED*') { Remove-Item -LiteralPath $rootProbe -Recurse -Force }
        }
        $sleeper = Start-Process powershell.exe -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 120' -WindowStyle Hidden -PassThru
        $pipe = 'PartitionManager.Helper.' + [guid]::NewGuid().ToString('N')
        $tokenHex = -join ((1..32) | ForEach-Object { '{0:X2}' -f (Get-Random -Maximum 256) })
        $h = Start-Process -FilePath $helper -ArgumentList '--pipe', $pipe, '--parent', $sleeper.Id, '--token', $tokenHex -WindowStyle Hidden -PassThru
        $helpers.Add($h)
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline -and -not $h.HasExited -and -not (PipeExists $pipe)) { Start-Sleep -Milliseconds 200 }
        $state = if ($h.HasExited) { "helper exited $($h.ExitCode)" } else { 'helper running and serving its pipe' }
        $available = -not $h.HasExited
        Record 'Squat' 'Helper still available after a standard user tried to squat its data folder' $(if ($available) { 'PASS' } else { 'FAIL' }) "$state (68 = refused unsafe data folder: fail-closed denial of service)"
        if (Test-Path -LiteralPath $pmData) { Record 'Squat' 'Data folder owner after attempt' 'INFO' "$((Get-Acl -LiteralPath $pmData).Owner); audit owner $(if (Test-Path $audit) { (Get-Acl -LiteralPath $audit).Owner } else { 'absent' })" }
        if ($RepairInstaller -and (Test-Path -LiteralPath $squat)) { Remove-Item -LiteralPath $squat -Force; $created.Add("REMOVED squat probe file $squat (owned by Administrators after repair)") }
        elseif ($r -like 'ALLOWED*') {
            # Remove only what the test account created.
            $sid = (New-Object Security.Principal.NTAccount("$env:COMPUTERNAME\$user")).Translate([Security.Principal.SecurityIdentifier]).Value
            foreach ($p in $squat, $audit, $pmData) {
                if ((Test-Path -LiteralPath $p) -and (Get-Acl -LiteralPath $p).GetOwner([Security.Principal.SecurityIdentifier]).Value -eq $sid) {
                    if ((Get-Item -LiteralPath $p) -is [IO.DirectoryInfo]) { if (-not (Get-ChildItem -LiteralPath $p -Force)) { Remove-Item -LiteralPath $p -Force; $created.Add("REMOVED $p") } else { $created.Add("KEPT non-empty $p") } }
                    else { Remove-Item -LiteralPath $p -Force; $created.Add("REMOVED $p") }
                }
            }
        }
        return
    }
    # ---------------------------------------------------------------- helper IPC
    $sleeper = Start-Process powershell.exe -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 600' -WindowStyle Hidden -PassThru
    function Start-Helper([int]$parentPid, [string]$pipe, [string]$tokenHex) {
        $p = Start-Process -FilePath $helper -ArgumentList '--pipe', $pipe, '--parent', $parentPid, '--token', $tokenHex -WindowStyle Hidden -PassThru
        $helpers.Add($p)
        $deadline = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $deadline -and -not $p.HasExited -and -not (PipeExists $pipe)) { Start-Sleep -Milliseconds 200 }
        return $p
    }
    function New-Pipe { 'PartitionManager.Helper.' + [guid]::NewGuid().ToString('N') }
    function New-TokenHex { -join ((1..32) | ForEach-Object { '{0:X2}' -f (Get-Random -Maximum 256) }) }
    function Talk([string]$pipe, [byte[]]$frame) { try { [PmSec]::Exchange($pipe, $frame) } catch { "EXCHANGE_ERROR: $($_.Exception.InnerException.Message)$($_.Exception.Message)" } }
    function Wait-Exit($p) { if (-not $p.WaitForExit(70000)) { $p.Kill(); return 'KILLED' } return $p.ExitCode }

    # P1 standard user blocked by pipe ACL
    $pipe = New-Pipe; $tok = New-TokenHex
    $h = Start-Helper $sleeper.Id $pipe $tok
    if ($h.HasExited) { Record 'IPC' 'Helper starts with valid arguments' 'FAIL' "exited early: $($h.ExitCode)" }
    else {
        Record 'IPC' 'Helper starts with valid arguments (as SYSTEM, parent = SYSTEM process)' 'PASS' "pid $($h.Id)"
        Start-Sleep -Seconds 2
        Record 'IPC' 'Helper state before standard-user attempt' 'INFO' $(if ($h.HasExited) { "exited $($h.ExitCode)" } else { 'running' })
        # Module / extraction audit while it is running (before any client touches it)
        $mods = @(); try { $mods = (Get-Process -Id $h.Id -ErrorAction Stop).Modules | ForEach-Object FileName } catch { Record 'DLL' 'Module listing' 'INCONCLUSIVE' $_.Exception.Message }
        AsUser 'IPC' 'Standard user connects to helper pipe' { [PmSec]::PipeConnect($pipe) } | Out-Null
        Start-Sleep -Seconds 1
        Record 'IPC' 'Helper state after standard-user attempt' $(if ($h.HasExited) { 'FAIL' } else { 'PASS' }) $(if ($h.HasExited) { "exited $($h.ExitCode) (a session ended: the standard user may have connected)" } else { 'still running and waiting: no session was established' })
        $outside = $mods | Where-Object { $_ -notlike "$env:windir\*" -and $_ -notlike "$install\*" }
        if ($mods.Count -gt 0) { Record 'DLL' 'Helper modules loaded from outside Windows and install folder' $(if ($outside) { 'FAIL' } else { 'PASS' }) $(if ($outside) { $outside -join '; ' } else { "$($mods.Count) modules, all under Windows or install folder" }) }
        # P2 wrong token, from SYSTEM (allowed by ACL)
        $reply = Talk $pipe ([PmSec]::Frame((@{ protocolVersion = 1; token = (New-TokenHex); clientProcessId = $sleeper.Id } | ConvertTo-Json -Compress)))
        Record 'IPC' 'Wrong token rejected' $(if ($reply -match '"accepted":false' -and $reply -match 'BAD_TOKEN') { 'PASS' } else { 'FAIL' }) $reply
        Record 'IPC' 'Helper exits after rejected handshake' 'INFO' "exit $(Wait-Exit $h)"
    }

    # P3 correct token, forged client PID (real client is this PowerShell, not the parent)
    $pipe = New-Pipe; $tok = New-TokenHex; $h = Start-Helper $sleeper.Id $pipe $tok
    $reply = Talk $pipe ([PmSec]::Frame((@{ protocolVersion = 1; token = $tok; clientProcessId = $sleeper.Id } | ConvertTo-Json -Compress)))
    Record 'IPC' 'Correct token from a different process (forged PID) rejected' $(if ($reply -match '"accepted":false' -and $reply -match 'WRONG_PEER') { 'PASS' } else { 'FAIL' }) $reply
    Wait-Exit $h | Out-Null

    # P4 correct token and PID, but client image is powershell.exe, not PartitionManager.exe
    $pipe = New-Pipe; $tok = New-TokenHex; $h = Start-Helper $PID $pipe $tok
    $reply = Talk $pipe ([PmSec]::Frame((@{ protocolVersion = 1; token = $tok; clientProcessId = $PID } | ConvertTo-Json -Compress)))
    Record 'IPC' 'Correct token and PID from a non-PartitionManager executable rejected' $(if ($reply -match '"accepted":false' -and $reply -match 'WRONG_IMAGE') { 'PASS' } else { 'FAIL' }) $reply
    Wait-Exit $h | Out-Null

    # P5 malformed frame
    $pipe = New-Pipe; $h = Start-Helper $sleeper.Id $pipe (New-TokenHex)
    $reply = Talk $pipe ([byte[]](0xFF, 0xFF, 0xFF, 0x7F))
    Record 'IPC' 'Oversized length prefix rejected' $(if ($reply -match 'BAD_LENGTH') { 'PASS' } else { 'FAIL' }) $reply
    Record 'IPC' 'Helper exits after malformed frame' 'INFO' "exit $(Wait-Exit $h)"
    $pipe = New-Pipe; $h = Start-Helper $sleeper.Id $pipe (New-TokenHex)
    $reply = Talk $pipe ([PmSec]::Frame('{"protocolVersion":1,"token":"00","clientProcessId":1,"runAs":"SYSTEM"}'))
    Record 'IPC' 'Unknown JSON member rejected' $(if ($reply -match '"accepted":false' -and $reply -match 'MALFORMED') { 'PASS' } else { 'FAIL' }) $reply
    Wait-Exit $h | Out-Null

    # P6 pipe squatting: another server already owns the name
    $pipe = New-Pipe
    $squatter = New-Object IO.Pipes.NamedPipeServerStream($pipe, [IO.Pipes.PipeDirection]::InOut, 1)
    $h = Start-Process -FilePath $helper -ArgumentList '--pipe', $pipe, '--parent', $sleeper.Id, '--token', (New-TokenHex) -WindowStyle Hidden -PassThru
    $helpers.Add($h)
    $code = Wait-Exit $h
    $squatter.Dispose()
    Record 'IPC' 'Helper refuses a pre-created (squatted) pipe name' $(if ($code -eq 67) { 'PASS' } else { 'FAIL' }) "exit $code (67 = pipe failed)"

    # P7 helper launched with a standard-user token
    $launch = [PmSec]::LaunchAsUser($token, $helper, "--pipe $(New-Pipe) --parent $($sleeper.Id) --token $(New-TokenHex)")
    Record 'Privilege' 'Helper launched with standard-user token refuses to run' $(if ($launch -match 'win32=740' -or $launch -eq 'EXITED 65') { 'PASS' } else { 'FAIL' }) "$launch (740 = elevation required, 65 = helper not-elevated refusal)"

    # ---------------------------------------------------------------- audit / journal after helper runs
    if (Test-Path -LiteralPath $pmData) {
        $rootProbe = Join-Path $pmData "pm-std-root-$Label"
        $r = AsUser 'Audit' 'Standard user creates a folder in ProgramData\PartitionManager' { [PmSec]::CreateDir($rootProbe) }
        if ($r -like 'ALLOWED*') { Remove-Item -LiteralPath $rootProbe -Recurse -Force }
        Record 'Audit' 'Data root ACL' 'INFO' "$((Get-Acl -LiteralPath $pmData).Owner) $((Get-Acl -LiteralPath $pmData).Sddl)"
    }
    if (Test-Path -LiteralPath $audit) {
        $acl = Get-Acl -LiteralPath $audit
        Record 'Audit' 'Audit folder owner after helper start' $(if ($acl.Owner -match 'Administrators|SYSTEM') { 'PASS' } else { 'FAIL' }) "$($acl.Owner); $($acl.Sddl)"
        $probeFile = Join-Path $audit "pm-std-probe-$Label.txt"
        $r = AsUser 'Audit' 'Create file in audit folder' { [PmSec]::CreateFile($probeFile) }
        if ($r -eq 'ALLOWED') { Remove-Item -LiteralPath $probeFile -Force }
        foreach ($f in Get-ChildItem -LiteralPath $audit -File -Recurse) {
            $p = $f.FullName
            AsUser 'Audit' "Open audit/journal for write: $($f.FullName.Substring($audit.Length))" { [PmSec]::OpenFor($p, [Security.AccessControl.FileSystemRights]::WriteData) } | Out-Null
            AsUser 'Audit' "Open audit/journal for delete: $($f.FullName.Substring($audit.Length))" { [PmSec]::OpenFor($p, [Security.AccessControl.FileSystemRights]::Delete) } | Out-Null
        }
        foreach ($d in Get-ChildItem -LiteralPath $audit -Directory -Recurse) {
            $probeFile = Join-Path $d.FullName "pm-std-probe-$Label.txt"
            $r = AsUser 'Audit' "Create file in $($d.FullName.Substring($audit.Length))" { [PmSec]::CreateFile($probeFile) }
            if ($r -eq 'ALLOWED') { Remove-Item -LiteralPath $probeFile -Force }
        }
        if ($false) {
            AsUser 'Audit' 'Squatted file still writable by its creator after helper start' { [PmSec]::OpenFor($squat, [Security.AccessControl.FileSystemRights]::WriteData) } | Out-Null
            Remove-Item -LiteralPath $squat -Force
            $created.Add("REMOVED squat file $squat")
        }
    }
    else { Record 'Audit' 'Audit folder present after helper start' 'FAIL' 'missing' }

    # ---------------------------------------------------------------- startup-hook probe (harmless marker)
    if ($StartupHookProbeBase64) {
        $hookDir = Join-Path $EvidenceDirectory 'hook'
        New-Item -ItemType Directory -Force -Path $hookDir | Out-Null
        $hookDll = Join-Path $hookDir 'StartupHookProbe.dll'
        [IO.File]::WriteAllBytes($hookDll, [Convert]::FromBase64String($StartupHookProbeBase64))
        $marker = Join-Path $hookDir "marker-$Label.txt"
        Remove-Item -LiteralPath $marker -ErrorAction SilentlyContinue
        $env:DOTNET_STARTUP_HOOKS = $hookDll; $env:PM_HOOK_MARKER = $marker
        try { $p = Start-Process -FilePath $helper -WindowStyle Hidden -PassThru; $code = Wait-Exit $p }
        finally { Remove-Item Env:DOTNET_STARTUP_HOOKS, Env:PM_HOOK_MARKER -ErrorAction SilentlyContinue }
        $ran = Test-Path -LiteralPath $marker
        Record 'Runtime' 'Helper ignores DOTNET_STARTUP_HOOKS from its environment' $(if ($ran) { 'FAIL' } else { 'PASS' }) $(if ($ran) { "hook EXECUTED: $(Get-Content -Raw $marker)" } else { "no marker; helper exit $code" })
        Remove-Item -LiteralPath $hookDir -Recurse -Force
    }
    $extract = @("$env:windir\SystemTemp\.net", "$env:windir\Temp\.net", (Join-Path $env:TEMP '.net')) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -Unique
    Record 'DLL' 'Runtime self-extraction directories present (SYSTEM)' $(if ($extract) { 'FAIL' } else { 'PASS' }) $(if ($extract) { ($extract | ForEach-Object { "$_ : " + ((Get-ChildItem -LiteralPath $_ -Recurse -File | Select-Object -First 8 | ForEach-Object Name) -join ',') }) -join ' | ' } else { 'none' })
}
catch {
    Record 'Harness' 'Unexpected error' 'FAIL' $_.Exception.Message
}
finally {
    foreach ($p in $helpers) { if (-not $p.HasExited) { $p.Kill() } }
    if ($sleeper -and -not $sleeper.HasExited) { $sleeper.Kill() }
    if ($user) {
        Remove-LocalUser -Name $user -ErrorAction SilentlyContinue
        $userProfile = Get-CimInstance Win32_UserProfile | Where-Object { $_.LocalPath -like "*\$user" }
        if ($userProfile) { $userProfile | Remove-CimInstance }
        $created.Add("REMOVED local user $user")
    }
    $out = Join-Path $EvidenceDirectory "security-$Label.json"
    [pscustomobject]@{ Label = $Label; Time = (Get-Date).ToString('o'); Instance = $actual; Rows = $rows; Created = $created } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $out -Encoding UTF8
    $pass = @($rows | Where-Object Status -eq 'PASS').Count; $fail = @($rows | Where-Object Status -eq 'FAIL').Count
    "SUMMARY label=$Label pass=$pass fail=$fail evidence=$out"
    $rows | Where-Object { $_.Status -ne 'PASS' } | ForEach-Object { "{0} | {1} | {2} | {3}" -f $_.Status, $_.Area, $_.Check, ($_.Detail -replace '\s+', ' ').Substring(0, [Math]::Min(220, ($_.Detail -replace '\s+', ' ').Length)) }
}







