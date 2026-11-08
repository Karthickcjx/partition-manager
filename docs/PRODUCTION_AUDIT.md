> Current 0.4.0-alpha status: [Milestone 3](reports/Milestone3.md). The following baseline audit/matrix contains historical statuses; the current report supersedes them. Move/Merge are blocked for real execution; initialization requires fresh blank-image approval.

# Production audit: PartitionManager 0.1.0 → 0.2.0-alpha

Date: 2026-11-07. Machine: Windows 11 Home 10.0.26200, Intel i5-11260H, 23.7 GB RAM, one NVMe system disk.
The session ran **unelevated**. The only MCP tools available were Claude-in-Chrome and Claude Docs; there was no
reverse-engineering, Ghidra or UI Automation MCP server.

## 1. Baseline (re-measured, not carried over)

| Check | Result |
|---|---|
| `dotnet build PartitionManager.sln` | Succeeded, 0 warnings |
| `dotnet test` | **106 passed, 0 failed, 0 skipped**, including the live read-only WMI discovery test |
| `scripts/ui-smoke.ps1` (UI Automation, launched process only) | **13 / 13 PASS** |
| Release artifacts present | `PartitionManager.exe` (self-contained), `PartitionManager-0.1.0-x64-setup.exe` |

The earlier SendKeys-based capture script that typed into a foreground window was deleted last session and is
not used. `ui-smoke.ps1` acts only through UI Automation patterns and, as of this audit, refuses any element
whose process ID is not the one it launched (`AssertOurs`).

## 2. Architecture findings (0.1.0)

* **Good foundations, kept as-is:** a pure `Core` (models, GPT/MBR parsers, layout validation, immutable
  operation records with `Apply(PreviewState)`, `OperationQueue`, `SafetyPolicy`); `Infrastructure` for Windows
  (WMI discovery, read-only raw reader); a WPF `Desktop` built around a single `IOperationExecutor` seam.
* **No privilege separation existed.** The only executor was the simulator; there was nowhere to put real writes safely.
* **Targets were identified by disk number and partition number only.** Both can change between inspection and
  execution (reboot, hot-plug, another tool). That's unsafe for any real write.
* No GPT partition GUID or disk location was captured, so a request couldn't be pinned to the exact device or partition.
* No defined cancellation semantics: a single Cancel button whose meaning wasn't specified per operation.
* Composition is manual (`App.OnStartup`), with no DI container. That's adequate at this size and flagged as debt.
* The log has no rotation or size cap, and it records full paths and model names (privacy).
* `ResizeMovePartitionOperation.Risk` is a constant `High`, even for a pure extend.
* The UI smoke test assumes the PC has C: and D: (it's machine-specific).
* About 290 MB working set at idle (see Performance.md). High for the workload.

## 3. Defects found and fixed in this milestone

| # | Severity | Defect | Fix |
|---|---|---|---|
| D1 | **Critical (security)** | The 0.1 installer was per-user (`%LOCALAPPDATA%\Programs`). With a privileged helper added, any unprivileged process could replace `PartitionManager.Helper.exe` and have its code elevated via the UAC prompt. | Installer is now per-machine (Program Files). The helper refuses to run (exit 68) from any folder a non-admin can write to (`InstallLocationCheck`, tested). |
| D2 | High (security) | An audit folder under `%ProgramData%` could be pre-created by a standard user with a permissive ACL. | The helper re-applies owner = Administrators and a protected ACL on every start. |
| D3 | High (integrity) | Disk and partition identified by number only. | `DiskIdentity` (unique ID, serial, size, style, SHA-256 layout fingerprint, image path) plus `PartitionTarget` (number, offset, size, GPT GUID). The helper re-enumerates and refuses on any mismatch or ambiguity. |
| D4 | Medium | Pressing Enter in a dialog, or invoking OK through UI Automation, didn't commit LostFocus-bound text boxes. | Fixed last session (`CommitTextBoxes`); now covered by the smoke test, which sets values through UI Automation. |
| D5 | Medium | Solution builds wrote to `bin\x64\...`, project builds to `bin\...`, so stale binaries got tested. | Fixed (`AppendPlatformToOutputPath=false`). |
| D6 | Low | The progress dialog's Cancel implied rollback. | Real execution says "Cancel remaining tasks" and explains that the task in progress always finishes. |

## 4. Security concerns still open

See [reports/SecurityAudit.md](reports/SecurityAudit.md). In short: no code signing; the VHD path is taken from
`MSFT_Disk.Location` and hasn't yet been confirmed on a real attached VHDX; the audit log isn't tamper-evident; a
same-user process that injects into the unelevated app can drive the helper within the virtual-disk gate.

## 5. Missing capabilities, by priority

1. **Run the virtual-disk test bench elevated.** This is the blocker for calling the label and letter operations
   "Tested on virtual disks".
2. **Reference-app UI capture.** This needs explicit authorization to run UI inspection elevated (the reference
   app runs elevated, and Windows UIPI blocks unelevated automation). Visual-comparison tests come after.
3. Real create, format, delete and initialize on virtual disks (`MSFT_Disk.CreatePartition`, `MSFT_Volume.Format`,
   `MSFT_Partition.DeleteObject`, `MSFT_Disk.Initialize`), each with VHDX tests.
4. Extend and shrink via `MSFT_Partition.Resize` plus `GetSupportedSize`.
5. Disk health: `MSFT_StorageReliabilityCounter`, NVMe and SMART health, reporting Unknown or Unsupported explicitly.
6. Image-file and VHDX cloning with bounded buffers and verification; then backup and restore of VHDX images.
7. OS migration, which needs disposable Windows VMs and a boot test.
8. Move, merge and split, which need data relocation and crash-safety design. They stay disabled until then.
9. DI container, log rotation, localisation, visual regression tests, code signing.

## 6. Milestones

| Milestone | Scope | State |
|---|---|---|
| M1 | Privileged helper, authenticated IPC, disk identity, virtual-disk gate, label and drive-letter writes | **Implemented in 0.2.0-alpha**; unit, IPC and failure-injection tests pass; VHDX round trip written but **not run** (needs admin) |
| M2 | Run M1 on VHDX elevated; fix whatever it finds | Blocked on authorization |
| M3 | Create, format, delete, initialize on VHDX | Not started |
| M4 | Extend and shrink on VHDX | Not started |
| M5 | Disk health toolkit (read-only) | Not started |
| M6 | VHDX and image cloning, backup and restore | Not started |
| M7 | Reference UI capture and visual regression | Blocked on authorization |
| M8 | Physical-disk validation programme | Requires separate explicit authorization |
