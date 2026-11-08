# Release gate — 0.5.1-preview (2026-11-07)

**Verdict: NOT releasable as a public product.** The release blockers are listed below. 0.5.1-preview is an unsigned engineering preview. Physical-disk writes stay disabled and real Move/Merge stays refused. Windows Server 2025 results do not stand in for Windows 10/11 compatibility testing. Independent security review, controlled physical testing and code signing are separate gates that are still open.

- **PASS:** verified on the stated environment with retained evidence.
- **FAIL:** a defect, or a gate that is not met.
- **NOT TESTED:** no faithful evidence yet.

## Build and static checks

| Gate | Result | Evidence / limitation |
|---|---|---|
| Unit and static regression tests (non-VHDX) | **PASS** 381/381 | `artifacts/reports/test-results.trx`. Includes the new `RuntimeHardeningTests` and `HelperStartupFailureTests`. |
| Real VHDX integration (elevated, dev host) | **PASS** 7/7 (0.5.0 code) | `artifacts/reports/m4-vhd-validated.trx`. Not rerun for 0.5.1: the 0.5.1 changes are runtime config, installer ACL, the environment guard and client error reporting, with no storage-engine change. `m4-tests.trx`, cited by older reports, is **missing**. |
| Dependency vulnerability audit | **PASS** (no known vulnerable packages reported) | `artifacts/reports/vulnerabilities.txt` |
| Code signing | **FAIL** | All binaries unsigned; no certificate available. |

## Installed-product security (disposable EC2 VM, SYSTEM-driven automation plus a temporary standard user)

| Gate | Result | Evidence / limitation |
|---|---|---|
| Upgrade 0.5.0 → 0.5.1, transfer integrity, installed hashes | **PASS** | `upgrade-0.5.1*-result.json` |
| Standard user cannot modify or plant files in the install folder (81 checks) | **PASS** | `security-final051-result.json` |
| Standard user cannot create or modify policy | **PASS** | same |
| Standard user cannot write audit or journal files, or create folders in the data root | **PASS** | same |
| Pre-install squat of `ProgramData\PartitionManager` | **FAIL** (residual) / repair **PASS** | `security-squatrepair051-result.json`. Windows lets Users create the folder before install. The installer repairs owner and ACL, and the helper fails closed (68). |
| Pipe denies other users; rejects bad token, forged PID, wrong image, oversize and malformed frames, and squatted pipe | **PASS** | `security-final051-result.json` |
| Helper refuses to run with a standard-user token | **PASS** (Windows 740) | same |
| `DOTNET_STARTUP_HOOKS` code execution in the elevated helper (F1) | **PASS** (fixed; FAIL on 0.5.0) | `security-baseline-4` (FAIL) vs `security-final051` (PASS) |
| CoreCLR profiler DLL loaded by the elevated helper from environment variables (F4) | **FAIL — release blocker** | `profiler-helperfinal051-result.json`; controls in `profiler-control-*2-result.json`. The environment guard (exit 69) is defence in depth only. The fix needs a clean helper launch environment (service or native entry point). |
| Helper loads only Windows or install-folder modules; no native self-extraction | **PASS** | same |
| Legitimate app → helper session through real interactive UAC (consent, `pmrdp-adm`) | **PASS** (A1) | Label PMRDPTEST→PMRDPA1; journal `SetVolumeLabel Prepared>StepStarted>StepReturned>Verified`; sentinels, boot disk, ACL and installed hashes unchanged (`state-after-A-result.json`). Over-the-shoulder elevation (S5) is still **NOT TESTED**. |
| UAC decline handling, confirmation and cancel, keyboard navigation, visible errors | **NOT TESTED** | RDP checks S2–S4, A2. The early-exit error reporting is unit-tested only. |
| Whether `HKCU\Environment` reaches the UAC-elevated helper (F4 exposure) | **CONFIRMED: it does** (A3) | A variable set non-elevated in `pmrdp-adm`'s HKCU\Environment reached the consent-elevated helper, which refused with exit 69 (screenshot from the user). Any medium-integrity code of an admin user can therefore inject a profiler DLL into the helper at the next UAC approval. F4 is exploitable in practice. Over-the-shoulder (A5) is still NOT TESTED. |
| Helper start-refusal dialog wording | **FAIL → fixed in source, not yet deployed** | The title said "changes may be partially completed" although nothing was sent. The new `ExecutionReport.NothingDispatched` gives the title "Nothing was changed"; covered by `IpcTests`. |

## Recovery and data integrity

| Gate | Result | Evidence / limitation |
|---|---|---|
| Journal fault injection, restart refusal, corrupt/torn records (unit) | **PASS** | `test-results.trx`. File-journal probe only, not a helper mutating a disk. |
| Verified VM recovery point before interruption tests | **PASS** | `snap-0cb61fae2de784052` of `vol-0bd0b07b3cf522d57` (60 GiB, crash-consistent, created with the instance running after user approval). `describe-snapshots`: completed 100% at 2026-11-07 21:34 IST. `artifacts/cloud-test-control/recovery-point.json`. A restore has not been exercised; that would cost extra. |
| OS restart with no operation in flight: installed hashes, data ACL, policy, boot-disk layout, journals, fixture layout and sentinel hashes | **PASS** | `state-pre-restart`, `state-post-restart`, `state-post-restart-reattached` results. The fixture VHDX detaches on restart by design (non-persistent attach) and re-attaches identical. |
| Real interruption/restart of the installed helper during VHDX mutation in the VM | **NOT TESTED** | Needs the real app to start the operation (RDP checklist R1–R5). `scripts/vm-interrupt-watcher.ps1` kills the helper, or restarts Windows, at `StepStarted`. |
| Hardware power-loss / device-disconnect recovery | **NOT TESTED** | Journal fault injection and VM restarts **do not** prove this. Needs controlled physical testing. |
| Move/Merge preservation of filesystem semantics | **FAIL** (unproven; real execution refused) | Unchanged. |

### Recovery point (created after approval)

- Snapshot `snap-0cb61fae2de784052`, about 23 GiB stored. Roughly US$1–2 per month at the ap-south-1 standard snapshot rate; check the AWS EBS pricing page.
- Delete it when no longer needed: `aws ec2 delete-snapshot --region ap-south-1 --snapshot-id snap-0cb61fae2de784052`.

## Physical-disk and platform gates

| Gate | Result |
|---|---|
| Physical enrollment, admin reauthentication, exact-operation consent | **FAIL** (not implemented; physical writes disabled) |
| Verified backup / recovery media / restore | **NOT TESTED** |
| Controlled physical-disk validation | **NOT TESTED** |
| Windows 10 / Windows 11 compatibility | **NOT TESTED** (only Windows Server 2025 VM and the dev host's Windows 11 build/test) |
| Independent security review | **NOT TESTED** (author self-review only) |
| Manual accessibility matrix (screen reader, high contrast, scaling) | **NOT TESTED** |

## Release blockers (in priority order)

1. **F4:** the elevated helper honours CoreCLR profiler environment variables. Re-architect the launch path so the helper starts with a clean environment.
2. Interactive UAC, IPC and UI checks are NOT TESTED (RDP checklist).
3. Real mid-operation interruption and restart tests on disposable VHDX in the VM (recovery point and clean-restart checks done; RDP checklist R1–R5).
4. Signing, independent review, Windows 10/11 matrix and controlled physical testing; physical authorization design is not implemented.
5. Move/Merge remain refused until preservation and recovery are proven.
