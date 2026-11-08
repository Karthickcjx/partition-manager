; Inno Setup 6 script. Build with scripts\build-release.ps1 (publishes first, then compiles this).
#define AppName "Partition Manager"
#define AppVersion "0.6.0-preview"
#define AppExe "PartitionManager.exe"

[Setup]
AppId={{6C1F3D3A-9F2B-4C1E-9B4E-2A7D5E8F1C0B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Partition Manager (clean-room)
DefaultDirName={autopf}\PartitionManager
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\artifacts\installer
OutputBaseFilename=PartitionManager-{#AppVersion}-x64-setup
SetupIconFile=..\src\Desktop\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Per-machine install into Program Files is required: the elevated helper must live in a folder that standard
; users cannot modify (the helper refuses to run otherwise). The app itself runs unelevated.
PrivilegesRequired=admin
VersionInfoVersion=0.6.0
VersionInfoProductTextVersion=0.6.0-preview

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\publish\PartitionManager.Helper.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "..\docs\reports\Milestone3.md"; DestDir: "{app}\docs\reports"; Flags: ignoreversion
Source: "..\docs\reports\Milestone4.md"; DestDir: "{app}\docs\reports"; Flags: ignoreversion
Source: "..\docs\reports\E_PHYSICAL_TEST_READINESS.md"; DestDir: "{app}\docs\reports"; Flags: ignoreversion
Source: "..\docs\reports\*-0.5.md"; DestDir: "{app}\docs\reports"; Flags: ignoreversion
Source: "..\docs\reports\ENGINEERING_AUDIT_0.5.md"; DestDir: "{app}\docs\reports"; Flags: ignoreversion
Source: "..\docs\reports\SecurityAudit-0.4.md"; DestDir: "{app}\docs\reports"; Flags: ignoreversion
Source: "..\docs\reports\InstallerValidation-0.4.md"; DestDir: "{app}\docs\reports"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; 0.6 public default: real operations on all disks (the helper still restricts the running Windows, EFI, MSR and
; recovery partitions to in-place shrink/extend). HKLM is writable only by administrators. Removed on uninstall.
Root: HKLM; Subkey: "SOFTWARE\PartitionManager\Helper"; ValueType: string; ValueName: "WriteMode"; ValueData: "AllDisks"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

; Inno's installation log removes only installed files. Never recursively delete
; {app}: it may contain unrelated user data. Logs and audit evidence are retained.

[Code]
{ The helper's audit/journal folder lives under ProgramData, where standard users may create folders. If a standard
  user pre-creates it before the helper's first run they own it, and the helper (correctly) refuses to start: a
  denial of service demonstrated on the 0.5.0-preview VM. Create and secure it here, before any file is copied, using
  only system tools (no scripts in user-writable temp folders). Failure aborts the installation. }
function RunSystemTool(const Exe, Params: String): Integer;
var
  Code: Integer;
begin
  if not Exec(ExpandConstant('{sys}\') + Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Log(Format('%s %s -> %d', [Exe, Params, Code]));
  Result := Code;
end;

procedure SecureDataDirectory();
var
  Root: String;
begin
  Root := ExpandConstant('{commonappdata}\PartitionManager');
  if not ForceDirectories(Root + '\Audit') then
    RaiseException('Could not create ' + Root + '\Audit');
  { Administrators take ownership of the whole tree (icacls enables the restore privilege; locale independent). }
  if RunSystemTool('icacls.exe', '"' + Root + '" /setowner *S-1-5-32-544 /T /C /Q') <> 0 then
    RaiseException('Could not set the owner of ' + Root);
  { Root: no inherited ProgramData rights; SYSTEM and Administrators full control, Users read/execute only. }
  if RunSystemTool('icacls.exe', '"' + Root + '" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /Q') <> 0 then
    RaiseException('Could not secure ' + Root);
  { Everything below inherits only from the secured root (removes any explicit rights a squatter added). }
  if RunSystemTool('icacls.exe', '"' + Root + '\*" /reset /T /C /Q') <> 0 then
    RaiseException('Could not reset permissions under ' + Root);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    SecureDataDirectory();
end;

