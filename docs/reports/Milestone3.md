# Milestone 3 engineering report — 0.4.0-alpha

Date: 2026-11-07, Asia/Calcutta. **Unsigned engineering artifacts produced;
release blocked; not production-ready.** Milestone 3 is not declared fully
complete: the isolated-VM installation/UAC matrix, interruption testing and
several security/accessibility gates remain open. Move/Merge are deliberately
unsupported for real execution, as permitted by the requested acceptance rules.

## Features and status

| Area | Accurate status | Result |
|---|---|---|
| GPT/MBR initialization | **Implemented / Tested on virtual disks** | Real MSFT_Disk.Initialize through helper engine; independently queried style, audit success and stale-request rejection. Requires a fresh approved blank-image identity; product approval provisioning UI remains pending. |
| Create/format/delete/shrink/extend/split; label/letters | **Tested on virtual disks** | Existing real operations re-run on disposable GPT and MBR images. |
| Move/Merge | **Simulated / Blocked** | Real helper requests refused. Existing staged prototype retained for development tests; consistency, ACL preservation and durable recovery are unproven. [Technical analysis](../MOVE_MERGE_LIMITATIONS.md). |
| Physical writes, including E: | **Blocked** | No exception or activation switch. All 13 allowlisted request kinds have negative physical-target tests. |
| E-scope policy | **Implemented design/rehearsal only** | Read-only inspector, stable identities, boundaries, protected snapshots, fresh proposal-bound confirmation and replay/expiry tests. No write authority. |
| Large sparse data test | **Tested on virtual disks** | 16 GiB VHDX; 8 GiB NTFS shrunk to 4 GiB; 1,003 surviving files hash-verified. |
| Layout-matched rehearsal | **Tested on virtual disks** | Seven source extents reproduced with documented provider adjustments; synthetic C/D/E counterparts; all protected deletion proposals rejected. |
| UI state/accessibility improvements | **Implemented; partial testing** | Real/simulated/unsupported/partial results distinguished; accessible preview label; 16 UI checks pass. |
| Installer | **Built / Not tested in VM** | Per-machine installer; recursive uninstall deletion removed. No host install/uninstall performed. |

## Fresh tests and reproducibility

Baseline: system dotnet contained no SDK; the existing user-local .NET 8 SDK was
used. **301 passed / 1 failed / 302 total** before source edits. The inherited
nonadjacent Merge test failed because a gap was treated as adjacency. Fixed in
the quarantined prototype; this is not a release claim for Merge.

Final ordinary Release tests: **336 passed, 0 failed, 0 skipped**, explicitly
excluding opt-in VirtualDisk and RealVolumeRecovery categories. Includes
protocol, identity, helper security, simulated operations, recovery logic,
experimental copier/staged fake-backend tests and new authorization regressions.
The old physical-volume recovery test was not run; its E: default was removed
and C/D/E or unapproved physical source volumes now fail before file creation.

Virtual-disk tests: **6 distinct integration tests passed in their latest runs**.
The full run's five successful tests are in `m3-vhd-third-attempt.trx`; its one
layout-replica failure was corrected and passed in `m3-vhd-layout-final.trx`.
`m3-integration-summary.json` maps each test to its latest evidence. Earlier
failures are retained, not relabeled as passes. Fixes involved read-only native
metadata handles and Windows' reserved prefix/partition-size rounding.

UI: **16/16 PASS**, both the Release development executable and the published
self-contained executable, in demo mode via owned-process UI Automation.
Final evidence: `artifacts/reports/m3-ui-published/ui-smoke-results.json`.
Release WPF build: zero warnings/errors. NuGet audit reported no vulnerable
packages against its configured source; this is not a complete binary audit.

Reproduce:

```powershell
$env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
dotnet test PartitionManager.sln -c Release --filter 'Category!=VirtualDisk&Category!=RealVolumeRecovery'
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-vhd-integration.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build-release.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\ui-smoke.ps1 -Exe artifacts\publish\PartitionManager.exe -Demo
```

VHDX creation is restricted to `D:\PartitionManager-TestDisks`, with exact path,
filename, size, reparse and free-space checks. The folder was absent before the
run; 130.78 GiB free was observed. Images were dynamic, ranging from 64 MiB to
16 GiB, plus a 512,177,299,456-byte sparse layout replica. All run-owned images
were detached and identity-checked before deletion; the empty folder was removed.
No original 4DDiG installation was modified.

## GPT/MBR and data integrity

- Initialization: GPT and MBR both passed; independent Get-Disk checks and audit
  evidence in `artifacts/reports/m3-initialization.md`.
- Label/assign/change/remove: GPT and MBR passed; latest full report
  `VirtualDiskRun-20261107-145258.md`.
- Create/shrink/extend/split/format/delete: six operations on each style passed;
  latest report `StructuralRun-20261107-145345.md`.
- **14/14 SHA-256 checkpoints**, each covering three files, passed across the
  structural workflows and injected split failure/recovery.
- **1,003/1,003 file hashes** matched after the large NTFS shrink, including a
  128 MiB individual file, 1,000 small files, Unicode, deep/long paths and read-only
  content. Streaming hashes and a 1 MiB write buffer; the VHDX file length was
  306,184,192 bytes before cleanup, not 16 GiB allocated.
- Replica: all source starts shifted by 32 MiB, capacity increased by 64 MiB for
  Windows' reserved GPT prefix. Synthetic C: size rounded up 713,216 bytes; D/E
  sizes exact. Three synthetic files hashed on corresponding NTFS data
  partitions, using temporary G-Z letters. No private host data copied.
- **7/7 protected deletion proposals rejected**, covering six host counterparts
  plus Windows' automatically created replica MSR. Host roles are annotations;
  actual replica host counterparts are BasicData entries.

Host metadata snapshots (`m3-host-before.json`, `m3-host-after.json`) match for
all physical partition numbers, GUIDs/types, starts, sizes and drive letters.
E: was not a test target and received no writes from this work. This is metadata
comparison and a no-write statement, not a full byte-for-byte E: comparison.

## Failure injection and recovery

Tested on real disposable GPT: create fails after Split's successful shrink;
helper reports partial completion and actual layout; extending back succeeds;
all three data hashes remain intact at both checkpoints. Disconnected and stale
targets are refused. Physical-disk refusal uses a backend that cannot write.

Ordinary tests cover access denied, I/O/disk-full copy exceptions, verification
corruption, missing copied files, cancelled copy, disconnect, helper/IPC crash
reporting, unexpected layout changes, stale/replayed authorization and a failed
shrink call that actually changed size. Prototype Move/Merge failures use fake
storage and temporary files; they do not prove safe virtual-disk migration.

**Not tested:** real power loss, helper/UI termination during a Windows storage
call, device-level I/O errors/removal during mutation, nearly full/fragmented
volumes, real permission-protected datasets and comprehensive locked-volume
behavior. No main-PC shutdown or simulated power failure was attempted.

## Security, installation and accessibility

See [SecurityAudit-0.4.md](SecurityAudit-0.4.md),
[InstallerValidation-0.4.md](InstallerValidation-0.4.md),
[Accessibility-0.4.md](Accessibility-0.4.md) and the
[E-scope design](../E_ONLY_AUTHORIZATION_DESIGN.md).

Fixed/contained findings include unsafe staged migration exposure, deletion of
unrelated installation files, misleading unchanged-state reporting, forced helper
termination after IPC closure, weak backing-path correlation and the helper's
user-writable installation override. Privileged audit paths now fail closed;
elevated code no longer logs into user-controlled LOCALAPPDATA.

Release blockers: unsigned binaries; independent security review; image-root
ownership/ACL enforcement; protected one-use initialization provisioning;
multi-step TOCTOU/recovery durability; installed DLL/ACL/UAC/pipe lifecycle
validation; screen-reader and DPI testing; controlled VM interruption tests.
No accessible isolated VM was identified, and no application installation was
performed on the host. Any critical security finding blocks release.

## Source files changed

- `Directory.Build.props`; `installer/PartitionManager.iss`.
- Core: `Execution/HelperPolicy.cs`, new `Safety/EScopeRehearsal.cs`.
- Infrastructure: `WindowsApi/RegistryHelperPolicySource.cs`, `Ipc/HelperClient.cs`.
- Helper: `Program.cs`, `Engine/WriteEngine.cs`, `WriteEngine.Staged.cs`,
  `WmiStorageWriteBackend.cs`, new `BackingImageLease.cs`,
  `InstallLocationCheck.cs`, `PolicyAndAudit.cs`.
- Desktop: `ViewModels/PartitionManagerViewModel.cs`,
  `ViewModels/Dialogs/ProgressDialogViewModel.cs`, `Views/DialogTemplates.xaml`.
- Tests: `Production/ExecutionFixtures.cs`, `StagedOperationTests.cs`,
  `VirtualDiskTests.cs`, `StructuralVirtualDiskTests.cs`,
  `IdentityAndRequestTests.cs`, `WriteEngineTests.cs`, new `Milestone3Tests.cs`,
  new `Milestone3VirtualDiskTests.cs`, `Recovery/RealVolumeRecoveryTests.cs`.
- Scripts: `run-vhd-integration.ps1`, `configure-test-environment.ps1`,
  `build-release.ps1`, new `write-sbom.ps1`.
- README, feature/audit/design documentation, test plan, migration limitations,
  current reports and historical-report pointers.

Existing untracked/modified source was preserved; no reset, clean or commit was
performed. Build informational version includes the repository's existing commit
identifier; it does not imply the current working tree is committed.

## Artifacts

All paths below are relative to `D:\projects\partition`:

| Artifact | Path |
|---|---|
| Unsigned self-contained WPF app | `artifacts/publish/PartitionManager.exe` |
| Unsigned privileged helper | `artifacts/publish/PartitionManager.Helper.exe` |
| Unsigned installer | `artifacts/installer/PartitionManager-0.4.0-alpha-x64-setup.exe` |
| SHA-256 manifest | `artifacts/checksums/SHA256SUMS.txt` |
| CycloneDX inventory | `artifacts/reports/bom.cdx.json` |
| NuGet dependency inventory/audit | `artifacts/reports/sbom.json`, `vulnerabilities.txt` |
| Ordinary test TRX | `artifacts/reports/test-results.trx` |
| VHDX evidence index | `artifacts/reports/m3-integration-summary.json` |

Both executables identify as 0.4.0-alpha; signatures are NotSigned. Installer
compilation is verified, not installation behavior. Checksums are recomputed
from the final artifacts. SBOM covers resolved runtime/package components and
identifies the linker as a non-shipped build tool; license review and a full
compiler/toolchain provenance inventory remain pending.
