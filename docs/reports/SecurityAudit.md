> Historical evidence through v0.3. For the freshly measured v0.4 results and current blockers, see [Milestone3.md](Milestone3.md).

# Security audit: privileged helper (0.2.0-alpha)

This is an internal design review backed by automated tests. It has **not** been reviewed by an independent
security team, and that review is a release gate.

## Threat model

* **Assets:** data on every attached disk; the running Windows installation; the administrator token.
* **Trust boundary:** the WPF app runs as the user at medium integrity. `PartitionManager.Helper.exe` runs
  elevated, on demand via UAC, for one session only. It installs no service, scheduled task or autostart entry.
* **Adversaries considered:** another unprivileged process in the same session; a malicious or buggy client
  message; stale UI state (disk renumbered, swapped or changed by another tool); a tampered installation folder.
* **Out of scope:** a process already running as administrator, which can do anything the helper can; kernel-level attackers.

## Controls and evidence

| Control | Implementation | Test evidence |
|---|---|---|
| Elevation required | `requireAdministrator` manifest; helper exits 65 if not elevated | Unelevated launch of the release helper fails with Win32 error 740 (observed) |
| Protected install location | Helper refuses (exit 68) if any non-admin SID can write its folder; installer is per-machine | `InstallLocationTests` (5) |
| Pipe ACL | Only the launching user's SID, Administrators and SYSTEM; NETWORK denied; one instance; `FirstPipeInstance` | Code review only (needs an elevated run to exercise) |
| No pipe squatting | Client checks `GetNamedPipeServerProcessId` equals the PID it started | Code review only |
| Peer authentication | 32-byte random token (constant-time compare), plus the real client PID from `GetNamedPipeClientProcessId` equals the launching PID, plus the client image equals `PartitionManager.exe` in the helper's folder | `IpcSessionTests`: wrong token, short token, stolen token from another PID, forged PID claim, impostor exe, requests before HELLO |
| Strict parsing | Length-prefixed JSON, 64 KiB cap, unknown members rejected, enum strings only | `ProtocolTests` (9): bad lengths, truncation, garbage, unknown members, unknown operation kind |
| Replay and freshness | Unique request ID, at most 2 min old, at most 30 s in the future | `ReplayGuardTests` (3), `Replayed_request_is_refused` |
| Allowlist | Only `SetVolumeLabel`, `AssignDriveLetter`, `RemoveDriveLetter`, `ChangeDriveLetter`; undefined values rejected | `WriteRequestTests` (12) |
| Independent revalidation | Helper re-enumerates via WMI, matches `DiskIdentity` (unique ID, serial, size, SHA-256 layout fingerprint), then `PartitionTarget` (number, offset, size, GPT GUID); refuses renumbered, ambiguous, resized or changed disks | `DiskIdentityTests` (11), `WriteEngineTests`: layout change, disconnect, substitution, partition GUID change |
| Release gate | Physical disks always refused (there's no setting for them). Virtual disks only if the backing image is under an admin-configured folder (HKLM), with UNC, device paths, `..` and drive roots refused, and the request's image path must match | `HelperPolicyTests` (14), `PHYSICAL_DISK`, `IMAGE_NOT_APPROVED`, `IMAGE_MISMATCH` tests |
| Policy cannot be set by users | Read from `HKLM\SOFTWARE\PartitionManager\Helper` (admin-writable); anything unreadable means Disabled | `Disabled_policy_rejects_before_touching_disks` |
| Safety policy reapplied | Same `SafetyPolicy` as the UI (system and boot volumes, BitLocker, read-only, offline) | `Safety_policy_is_reapplied_in_the_helper` |
| Verification | Re-enumerates after writing; a mismatch is reported as Failed/UNVERIFIED | `Write_that_does_not_take_effect_fails_verification` |
| Partial failure | A letter change is remove + add; if the add fails, the old letter is restored and the outcome reported | `Failed_second_half_of_letter_change_restores_the_old_letter` |
| Audit trail | JSON lines in `%ProgramData%\PartitionManager\Audit`; folder owner and ACL re-enforced on start | STARTED/SUCCEEDED/FAILED/REJECTED entries asserted in tests |
| Dependency vulnerabilities | `dotnet list package --vulnerable --include-transitive` (NuGet audit) | No vulnerable packages reported (`artifacts/reports/vulnerabilities.txt`) |

## Residual risks (open)

1. **Unsigned binaries.** The UAC prompt shows "Unknown publisher", so users can't verify the helper. There's no
   certificate available. Signing is a release gate.
2. **Image-path source unconfirmed.** The gate reads the backing file from `MSFT_Disk.Location`. That's expected
   for attached VHDs but not yet observed on this machine, because the VHDX test needs admin. If `Location` isn't
   the path, the gate fails closed (refuses), not open. Planned hardening: cross-check with
   `GetStorageDependencyInformation`.
3. **Same-user code injection.** A malicious process running as the same user could inject into the unelevated
   app after the helper is approved, and send requests. Impact is limited to allowlisted metadata changes on
   approved VHDX files.
4. **Check-to-write window.** Milliseconds pass between revalidation and the WMI call; another admin tool could
   change the disk in that window. Post-write verification detects most such races.
5. **Audit log isn't tamper-evident.** Admins can edit it. A hash chain or the Windows Event Log is planned.
6. **Token on the command line.** It's readable only by processes able to read an elevated process's memory
   (i.e. administrators), and it's also bound to PID and image.
7. **`AllowUnprotectedLocation=1`** (dev override) disables control D1. `configure-test-environment.ps1` sets it
   only with `-DevBuild`.

## Not done

Fuzzing of the protocol parser beyond the cases above; an elevated end-to-end run of the real pipe ACL and UAC
path; an independent penetration test; code signing; third-party licence review of the full SBOM (the shipped
runtime dependencies are System.Management and System.CodeDom, both MIT, plus the .NET runtime, MIT).

## 0.3.0-alpha additions (structural operations)

Allowlist extended with `CreatePartition`, `FormatVolume`, `DeletePartition`, `ShrinkPartition`, `ExtendPartition`
and `SplitPartition`. All existing IPC controls, process-identity checks, the UAC requirement and the helper location
check are unchanged. The physical-disk gate is unchanged, so every structural kind is refused on physical disks
(test `Physical_disk_is_refused_for_every_structural_kind`).

New server-side controls:
* Per-kind strict validation: required and forbidden fields, 1 MiB alignment, sizes inside the disk, no shrink that
  grows and no extend that shrinks, file system on the allowlist (NTFS, FAT32, exFAT).
* Windows' own `GetSupportedSize` bounds every shrink, extend and split (`BELOW_MINIMUM`, `NOT_ENOUGH_SPACE`).
* Create must lie entirely inside freshly enumerated unallocated space.
* **Out-of-scope change detection:** after every structural operation, every other partition's number, offset, size
  and GUID must be unchanged, otherwise `OTHER_PARTITIONS_CHANGED`, with an audit entry.
* Disk disappearance during an operation is reported as `DISK_DISCONNECTED`, with the state described as unknown.
* Any exception inside a step (WMI, COM, device removal) becomes a reported, stopped step, never an unhandled crash.
* One structural change per disk per execution, because later partition numbers and GUIDs can't be predicted.

Security and failure tests added (all passing; see TestResults.md):

| Scenario | Test(s) |
|---|---|
| Forged disk identity | `Forged_disk_identity_is_rejected` |
| Drive-letter reassignment | `Drive_letter_reassignment_does_not_redirect_the_operation` |
| Changed partition GUID | `Changed_partition_guid_is_rejected` |
| Unexpected partition boundaries | `Unexpected_partition_boundaries_are_rejected` |
| Concurrent disk modification | `Concurrent_layout_change_before_execution_is_rejected`, `Change_to_another_partition_during_the_operation_is_detected`, real VHDX stale-layout test |
| Invalid shrink and resize sizes | `Invalid_resize_sizes_are_rejected` (4 cases), `Shrink_below_windows_minimum_is_rejected`, real VHDX `BELOW_MINIMUM` |
| Partial split completion | `Split_failing_at_{shrink,create,format,letter}_*`, `Split_whose_shrink_does_not_take_effect_*`, real VHDX forced create failure |
| Helper crash | `Helper_crash_during_a_structural_operation_is_reported_as_uncertain` |
| Interrupted operation and disconnection | `Disk_disconnected_mid_split_is_reported`, real VHDX detach-then-request |
| Unsupported file systems | `Shrinking_fat32_is_rejected_as_unsupported`, `Unsupported_file_system_is_rejected` |
| Safety policy for structural kinds | `Delete_of_a_boot_partition_is_blocked_by_safety_policy` |

Residual risks specific to structural operations:
* **No rollback.** Split is a sequence of separate Windows calls. A failure leaves a reported intermediate state
  plus manual recovery steps; only the "extend back" recovery was exercised on a real disk.
* The out-of-scope check compares WMI's view of partitions. It doesn't yet raw-read the GPT. That's planned for E:
  (see the E:-only design), where a disk-wide table rewrite is the main risk.
