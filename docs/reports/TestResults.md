> Historical evidence through v0.3. For the freshly measured v0.4 results and current blockers, see [Milestone3.md](Milestone3.md).

# Test results: 0.2.0-alpha (2026-11-07)

Run on Windows 11 Home 10.0.26200, unelevated, Release configuration (`scripts/build-release.ps1`).

## Automated tests (`dotnet test`)

**210 total: 209 passed, 0 failed, 1 skipped.** The baseline before this milestone was 106 (all still passing).

| Test class | Count | Result | Covers |
|---|---|---|---|
| PartitionTableTests | 17 | pass | MBR, EBR chains, GPT, CRC32, corruption detection |
| LayoutTests | 29 | pass | Sizes, segments, alignment, bounds, MBR limits |
| DiskMapWidthTests | 5 | pass | Disk-map width allocation |
| OperationTests | 26 | pass | Planning and validation of every operation |
| SafetyTests | 11 | pass | System and boot protection, BitLocker, read-only, health |
| QueueTests | 7 | pass | Preview, replay, dependent-task removal, rebase |
| ExecutorTests | 3 | pass | Simulator progress, cancellation, failure |
| InfrastructureTests | 8 | pass | Bus types, logical partitions, file log, **live read-only WMI discovery** |
| DiskIdentityTests | 11 | pass | Fingerprint, match, ambiguity, renumbering, substitution |
| WriteRequestTests | 12 | pass | Allowlist, field smuggling, overflow, mapping |
| HelperPolicyTests | 14 | pass | Image-folder confinement, traversal, UNC, device paths |
| EligibilityTests | 6 | pass | Real vs. simulated routing |
| ProtocolTests | 9 | pass | Framing, size limits, malformed and unknown fields |
| ReplayGuardTests | 3 | pass | Replay, stale, future requests |
| WriteEngineTests | 20 | pass | Full helper pipeline with fakes, plus failure injection (access denied, disconnect, concurrent layout change, write failure, partial failure, failed verification) |
| IpcSessionTests | 10 | pass | Real named pipe in-process: authentication, peer checks, replay, malformed frames |
| HelperExecutorTests | 5 | pass | Client executor: success, rejection, cancellation semantics, declined UAC, helper crash |
| HelperArgumentTests | 7 | pass | Helper command-line validation |
| InstallLocationTests | 5 | pass | Refusal to run from user-writable folders |
| VirtualDiskTests | 2 | **1 pass, 1 skipped** | VHDX creation (unprivileged) passed. **The VHDX attach → initialize → format → label/assign/change/remove round trip was skipped: it requires administrator rights and `PM_VHD_TESTS=1`.** |

## End-to-end UI (`scripts/ui-smoke.ps1`)

Against the Release single-file exe: **14 / 14 PASS**. The checks are: main window, discovery, safety block on
C:, resize dialog, shrink queued, freed space shown, create dialog, create queued, confirmation shows the target
identity, **physical-disk tasks routed to simulation**, progress, completion, simulated-result banner, Disk
Information.

Interaction is through UI Automation only. Each element's process ID is checked against the launched process
before any action. No keyboard or mouse input is synthesized.

## Other checks

* Unelevated launch of `PartitionManager.Helper.exe` fails with Win32 error 740 ("requires elevation"), as
  intended (observed).
* NuGet vulnerability audit: no vulnerable packages.
* The installer **builds**. It was **not** installed or uninstalled on this machine, so the install and
  uninstall gates are untested.

## Not run

* (The virtual-disk integration test was later run elevated; see the next section.)
* The real UAC and pipe-ACL path between the release app and the release helper (needs admin and a configured policy).
* Accessibility audit with a screen reader (Narrator/NVDA), and DPI scaling at 150% and 200%.
* Any test on physical disks. None are planned without separate authorization.

## Real virtual-disk runs (elevated, 2026-11-07)

Script: `scripts/run-vhd-integration.ps1` (one UAC approval per run). Images live in `D:\PartitionManager-TestDisks`
only. D: (Disk 0, physical NVMe, also the Windows system disk) was used purely as file storage and was never a write
target. Each result counts only if the helper reported success **and** an independent `Get-Partition`/`Get-Volume`
query showed the expected value. Reports: `artifacts/reports/VirtualDiskRun-<runid>.md` and `.json`.

| Run | Result | Findings |
|---|---|---|
| 20261107-131841 | FAIL | **Bug found:** `AddAccessPath` returned status 5 (invalid parameter), because `AccessPath` and `AssignDriveLetter` were both passed. Assign, change and remove failed. **Bug found:** native struct padding missing in `CREATE_VIRTUAL_DISK_PARAMETERS`; the cleanup identity check refused to delete. Label, the stale-layout refusal and the Disk 0 refusal passed. |
| 20261107-132031 | FAIL (cleanup only) | All 8 operations passed on GPT and MBR. Cleanup refused: `GET_VIRTUAL_DISK_INFO_IDENTIFIER` changes between creation and detach, so it can't be used as identity. |
| 20261107-132147 | **PASS** | Label, assign, change and remove on GPT and MBR (8/8, independently verified); stale-layout request refused on both; write request for D:/Disk 0 refused with zero backend calls (D: label unchanged); both images detached; both deleted after their VHDX virtual-disk ID matched the manifest. |

After the runs, a read-only check showed D: unchanged (label, size, offset, partition number), Disk 0's six
partitions identical to the pre-test inspection, and no virtual disks attached.

**Not covered:** the shipped app driving the helper through UAC and the named pipe (the test ran the write engine
in-process with the real WMI backend); create, format, delete, initialize, extend and shrink, which have no real
implementation (initialize, create and format were run with Windows cmdlets only, as setup); cloning and recovery,
which have no engine.

## Data recovery on a real volume (elevated, 2026-11-07, run 20261107-134103)

E: is a 55.75 GB NTFS partition (Disk 0, partition 7). The test created four files with random content through normal
file APIs, deleted them together with their folders, flushed the volume, then read E: raw (read-only) and recovered to
`D:\PartitionManager-Recovered\20261107-134103`.

| File | Size | Storage | Chance | Result |
|---|---|---|---|---|
| tiny.txt | 300 B | resident in MFT | Excellent | SHA-256 match |
| doc.bin | 1 MiB | 1 run | Good | SHA-256 match |
| sub\photo.jpg (deleted subfolder) | 3 MiB | 1 run | Good | SHA-256 match, folder path rebuilt |
| video.bin | 24 MiB | 1 run | Good | SHA-256 match (45 ms) |

* Lost-partition search: a 256 MB NTFS partition created and then deleted on a VHDX (stored on E:) was found at its
  exact offset (16,777,216) and size (268,435,456), confidence High, in 22 ms.
* Benchmark (read-only) of D:, 270 GB: 321,280 MFT records scanned in 0.98 s; 87,637 deleted entries listed (counts only).
* Unit coverage: 24 recovery tests on hand-built NTFS images (boot sector, data runs, fixups, torn records, 2,000 random
  records as a no-crash check, resident, fragmented, sparse and valid-data-length files, reused-cluster detection,
  name sanitising, cancellation, FAT32/exFAT/NTFS lost-partition detection).
* Not covered: files whose MFT record was reused (no carving), attribute-list files, FAT/exFAT file recovery,
  restoring found partitions, and the scan run from inside the elevated app UI.

## 0.3.0-alpha: structural operations (2026-11-07)

**Unit and integration tests (unelevated):** 274 passed, 0 failed, 4 skipped (admin-only). The new structural engine
tests use a fake backend with realistic semantics and injected failures, plus IPC and executor tests.

**Real virtual-disk run 20261107-141235** (elevated, `scripts/run-vhd-integration.ps1`; images created in a new
`D:\PartitionManager-TestDisks` with your approval, deleted afterwards, folder removed):

| Area | Result |
|---|---|
| Create, Shrink, Extend, Split, Format, Delete on GPT VHDX | 6/6 PASS (independent `Get-Partition`/`Get-Volume`) |
| Same six on MBR VHDX | 6/6 PASS |
| Negative: create over a partition, shrink below the Windows minimum, extend past the disk, stale layout | 4/4 refused, layout unchanged |
| Real partial failure: Split with create forced to fail after the real shrink | Reported `SPLIT_CREATE_FAILED` with the actual layout and recovery steps |
| Documented recovery: extend back | PASS |
| Disconnection: request after detaching the disk | `IDENTITY_NOTFOUND`, nothing written |
| Data integrity: SHA-256 at 14 checkpoints | 14/14 match ([DataIntegrity.md](DataIntegrity.md)) |
| Disk 0 (host of D:, C:, E:) before vs after | identical |
| Label and letter regression run (20261107-141208) | PASS |

A first attempt (run 20261107-141033) passed everything except the partial-failure test. That test asked to keep
236 MB when Windows' minimum was 250 MB, so the engine correctly refused before writing. The test was fixed to use
Windows' reported minimum and the run was repeated.

**Not covered:** cancellation on real disks (structural operations aren't cancellable once started; cancellation
before execution is unit-tested); interruption inside a Windows call; the shipped app driving these through UAC and
the pipe; physical disks (disabled).
