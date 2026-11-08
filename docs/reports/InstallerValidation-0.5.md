# Installer validation — 0.5.0-preview

Update: [AWS Windows Server 2025 validation](AWS-InstallerValidation-20261107.md) now passes fresh install, repair rerun, uninstall/sentinel preservation, reinstall, installed binary hashes, and helper missing-argument rejection. It supersedes the no-VM/lifecycle-pending status below. Interactive UAC, standard-user, Windows 10/11 and recovery checks remain pending.

Installer compilation and executable hashing are build checks, not installation validation. No disposable Windows VM was available in this session, and the host was not installed, repaired, upgraded or uninstalled.

`scripts/validate-installer-vm.ps1` requires both explicit disposable-VM confirmation and a recognized VM model, plus elevation inside that VM. Without `-RunLifecycle` it only collects preflight evidence. With it, the harness requires no existing product directory, runs fresh installation, repair rerun, uninstall with an unrelated-file sentinel, and reinstall; captures logs, executable placement and ACLs. Run only after taking a disposable VM snapshot:

```powershell
.\scripts\validate-installer-vm.ps1 -Installer C:\Test\PartitionManager-0.5.0-preview-x64-setup.exe -EvidenceDirectory C:\Test\Evidence -DisposableVmConfirmed -RunLifecycle
```

The harness has been syntax-checked, not executed. All lifecycle outcomes remain PENDING. Separately test upgrade from v0.4 on another snapshot, standard/admin launches, UAC approve/reject, authenticated helper IPC, standard-user replacement of helper/dependencies, extraction/DLL search paths, restart, and uninstall preservation. The script explicitly leaves those interactive checks pending. Retain VM snapshot ID, OS version, installer hash, account privileges and logs with results. Unsigned status must remain visible.
