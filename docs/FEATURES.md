> Current 0.5.0-preview status: [Milestone 4](reports/Milestone4.md). Historical VHDX results below predate the new journal and are not current-version validation. Move/Merge and physical writes remain blocked; initialization requires fresh blank-image approval. The follow-up elevated VHDX run passed all 7 tests; see [current evidence](reports/ProductionHardening-20261107.md).

| v0.5 addition | Current status |
|---|---|
| Durable intent, per-step observed layout, interrupted-history refusal | Implemented; ordinary journal/fault/process tests pass; GPT/MBR real VHDX journal test passed |
| Immutable E:-region scope and protected-partition model | Implemented/tested in virtual-only model; physical authorization not implemented |
| Complete before/after layout in confirmation | Implemented; UI smoke passed |
| Scrollable dialogs, safe initial focus, high contrast palette | Implemented; manual accessibility/DPI checks pending |
| VM installer lifecycle harness | Implemented and syntax-checked; VM execution pending |

# Feature matrix and roadmap

The **Evidence** column says how the feature was confirmed in the reference app: **S** means its UI strings exist
in `Languages\en-US.xaml`, **M** means a matching native module or tool is shipped, and **V** means it was seen
on screen. A feature counts as present in the reference app only when it has at least one of these.

Status in this project. These labels are never used interchangeably:
* **Done (read-only)** means implemented and covered by tests; it never writes.
* **Simulated** means it can be planned, validated and previewed; execution is rehearsed without writing anything.
* **Implemented** means a real write path exists (privileged helper, approved virtual disks only) and passes unit,
  IPC and failure-injection tests with fakes, but has **not** yet run against a real VHDX.
* **Tested on virtual disks** means it passed the elevated VHDX integration test, with every result confirmed by an independent `Get-Partition`/`Get-Volume` query. Reached by: label, assign/change/remove drive letter. The write engine ran in-process with the real WMI backend; the UAC + named-pipe path from the shipped app has not been exercised yet.
* **Validated on physical disks** and **Production-ready** apply to nothing yet.
* **Planned** means it has a placeholder page only.

## A. Partition management

| Feature | Evidence | Reference behaviour | Implementation approach (documented APIs) | Risk | Status | Tests |
|---|---|---|---|---|---|---|
| View disks and partitions | V S | Disk map plus partition list | WMI `MSFT_Disk` / `MSFT_Partition` / `MSFT_Volume` / `MSFT_PhysicalDisk` | Read-only | **Done** | `Wmi_discovery_*` (live integration), layout tests |
| Partition properties | S | Info dialog | Same data, plus layout validator | Read-only | **Done** | — (UI) |
| Create partition | S M | Size, letter, label, file system | `MSFT_Disk.CreatePartition` + `MSFT_Volume.Format` + `AddAccessPath` via helper | Medium | **Tested on virtual disks** (GPT + MBR, run 20261107-141235); simulated on physical disks | `StructuralEngineTests`, `StructuralVirtualDiskTests` |
| Delete partition | S M | Task-list item | `MSFT_Partition.DeleteObject` via helper | Destructive | **Tested on virtual disks** (run 20261107-141235); delete-all is simulated only | same |
| Shrink / extend | S M | Front/size/rear fields | `MSFT_Partition.GetSupportedSize` + `Resize` via helper (start offset fixed) | High | **Tested on virtual disks** (run 20261107-141235), file hashes intact | same |
| Move partition | S M | Front space edit | No universal Windows API; staged prototype blocked pending consistency and recovery | High | **Simulated** | `Move_steps_*` |
| Split partition | S | Shrink plus create | Shrink → verify freed space → create → format → letter → verify (not atomic; partial states reported with recovery steps) | High | **Tested on virtual disks** (run 20261107-141235), incl. real partial failure + recovery | same |
| Merge partitions | S | Moves files into a folder on the target | File copy, delete, extend (future) | High | **Simulated** | `Merge_*` |
| Format | S M | File system, label, cluster, full/quick | `MSFT_Volume.Format` via helper | Destructive | **Tested on virtual disks** | `StructuralVirtualDiskTests` |
| Assign / change / remove drive letter | S | Pick a free letter | `MSFT_Partition.AddAccessPath` / `RemoveAccessPath` via helper | Low | **Tested on virtual disks** (GPT and MBR VHDX, run 20261107-132147); simulated elsewhere | `VirtualDiskTests` + unit/IPC/failure-injection tests |
| Change label | S | — | `MSFT_Volume.SetFileSystemLabel` via helper | Low | **Tested on virtual disks** (same run); simulated elsewhere | Same as above |
| Hide / show | S | Task-list item | GPT attribute bits / MBR hidden types, remove access path (future) | Low | **Simulated** | `Hide_then_show` |
| Initialize disk (MBR/GPT) | S | Confirmation dialog | `MSFT_Disk.Initialize`; fresh administrator blank-image approval required | Destructive | **Tested on virtual disks**; UI approval provisioning pending | `Milestone3VirtualDiskTests` |
| Convert primary ↔ logical | S | — | Requires EBR editing | High | Planned | — |
| Check file system | — | Not confirmed in the strings | `MSFT_Volume.Repair` (scan only) | Low | Not in reference; not planned | — |

## B. Disk management

| Feature | Evidence | Approach | Risk | Status | Tests |
|---|---|---|---|---|---|
| Detect disks, SSD/HDD, bus, capacity | V S | `MSFT_PhysicalDisk.MediaType`, `MSFT_Disk.BusType` | Read-only | **Done** | `Bus_type_*`, integration |
| Partition-table parsing (MBR, EBR chain, GPT with CRC32 and backup header) | M | Own parser over a read-only `\\.\PhysicalDriveN` handle (admin) | Read-only | **Done** | `PartitionTableTests` (17) |
| Raw table vs. Windows view check | — | Disk Information page, "Verify partition table" | Read-only | **Done** (admin only) | Parser tests; not run elevated here |
| BitLocker detection | S M | `Win32_EncryptableVolume` (admin); otherwise "unknown" and a warning | Read-only | **Done** | `Bitlocker_blocks_resize` |
| MBR ↔ GPT conversion | S M | `mbr2gpt.exe` (system disk), table rewrite (data disk) | High | Planned | — |
| Disk health / SMART | S M | `MSFT_StorageReliabilityCounter`, `IOCTL_STORAGE_QUERY_PROPERTY` | Read-only | Planned (health status flag is shown) | — |
| Bad-sector scan | S M | Sequential raw reads | Read-only | Planned | — |

## C. OS migration, D. cloning and backup

| Feature | Evidence | Approach | Risk | Status |
|---|---|---|---|---|
| Clone OS disk (system migration) | V S M | Clone ESP, MSR, OS and recovery partitions, then `bcdboot` | High | Planned |
| Clone data disk / partition | V S M | Sector copy using the volume bitmap (`FSCTL_GET_VOLUME_BITMAP`), then verify | High | Planned |
| Application migration | V M | — | High | Out of scope for now |
| Backup / restore (system, disk, files) | S M | VSS snapshot plus image file | Medium | Planned |

## E. Other utilities found in the reference app

| Feature | Evidence | Status |
|---|---|---|
| Deleted-file recovery (NTFS) | S M | **Tested on a real volume** (E:, 4 files bit-exact) |
| Partition recovery (lost-partition scan) | S M | **Scan tested on virtual disk**; restore not implemented |
| Data wipe (shred) | S M | Planned |
| NTFS ↔ FAT32 conversion | S M | Planned |
| Boot repair (UEFI) | S M | Planned |
| BitLocker unlock / key search | S M | Not planned (security-sensitive) |
| WinPE bootable media, Windows download/upgrade, Windows-to-Go, Windows 11 "bypass" | S M | Not planned (outside partition management; bypassing installer checks is not something this project will ship) |
| Windows Clean (junk files) | V M | Not planned |

## Safety controls

* Real writes are disabled by default. They can be enabled only by an administrator (HKLM policy), only for VHD/VHDX
  files in approved folders, for implemented metadata/structural operations. **No configuration enables physical disks.**
* Every operation is validated structurally (bounds, overlap, 1 MiB alignment, MBR four-primary and 2 TB limits,
  file-system size and label rules) **and** against `SafetyPolicy`, in the UI *and again in the helper*:
  * blocks deleting or formatting the running system's volumes and the EFI, MSR and recovery partitions;
  * blocks wiping or initializing the system disk, moving the boot volume, and changing its letter;
  * blocks restructuring BitLocker-encrypted volumes;
  * blocks any change to read-only or offline disks, and high-risk work on unhealthy disks.
* Execution pipeline: DISCOVER → VALIDATE → PLAN → PREVIEW → CONFIRM (UI) → REVALIDATE → EXECUTE → VERIFY →
  REPORT (helper). The helper trusts nothing from the UI and re-derives disk identity from a fresh enumeration.
* Execute shows the target identity (disk number, model, serial number, size, SYSTEM DISK flag), the mode
  (simulation or real) with its reason, every warning, and a backup recommendation.
* Cancellation semantics. Simulation stops at any step boundary. Real execution is *before execution only*:
  cancel skips the tasks after the current one, and the task in progress always finishes and is verified.
* A letter change is two calls. If the second fails, the helper restores the old letter and reports what happened.
* Audit trail in `%ProgramData%\PartitionManager\Audit` (admin-only write); UI log in `%LOCALAPPDATA%`.

### Per-operation recovery notes (real-write operations)

| Operation | Preconditions | Failure / partial state | Recovery | Verification |
|---|---|---|---|---|
| Set label | Volume mounted; label valid for its file system | Label unchanged | Retry | Re-read label |
| Assign letter | Partition has no letter; letter free | No letter assigned | Retry | Re-read access paths |
| Remove letter | Current letter matches the request | Letter still present | Retry | Re-read access paths |
| Change letter | Current letter matches; new letter free | Old letter removed but new one not added | Helper re-adds the old letter automatically; if that fails too, the user is told the partition has no letter and can assign one | Re-read access paths |

## Roadmap

1. ~~Elevated worker process~~: done in 0.2.0-alpha (privileged helper, authenticated IPC).
2. **Run the VHDX test bench elevated** (`VirtualDiskTests`) and fix whatever it finds. This is what moves label
   and letter to *Tested on virtual disks*.
3. Real create, format, delete and initialize on VHDX, then extend and shrink with `GetSupportedSize`.
4. Disk health (reliability counters, NVMe/SMART) with explicit Unknown and Unsupported states.
5. VHDX and image cloning with verification, then backup and restore, then OS migration (VM boot test required).
6. Move, merge and split execution, which needs data relocation and crash-safe journaling.
7. Reference UI capture (needs authorization to run inspection elevated) and visual regression tests.
8. Code signing, localisation, DI container, log rotation, memory reduction.
