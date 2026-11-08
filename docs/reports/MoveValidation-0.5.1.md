# Real Move (block-level) — validation, 0.5.1-preview (2026-11-07)

**Status: implemented and VM-validated on disposable VHDX; independent review in progress; not yet exercised through the app UI.**
Real Move is enabled only for attached VHD/VHDX disks in the administrator-approved folder. Physical disks are refused by the
engine regardless of policy. Merge remains simulation-only. Nothing here was run on a physical disk or the host.

## Design

The earlier staged file-copy prototype was replaced (its blockers are listed in [MOVE_MERGE_LIMITATIONS](../MOVE_MERGE_LIMITATIONS.md)).
The new Move copies **every sector** of the partition, so file data, ACLs, alternate data streams, object IDs, the volume serial
number and (GPT) the partition GUID are preserved by construction.

1. Engine gates (unchanged pipeline): request validation, HKLM policy, live re-discovery, disk identity, physical-disk refusal,
   approved-image check, image pinning, safety policy, then an intent journal entry.
2. Exclusive volume lock (`FSCTL_LOCK_VOLUME`, fails if any handle is open: *nothing changed*) and dismount, held to the end.
3. SHA-256 of the source extent; chunked copy in an overlap-safe order (descending when moving right, ascending when moving
   left, chunk ≤ shift distance), each chunk flushed and read back; one durable journal checkpoint per chunk.
4. Full destination SHA-256 must equal the source hash; boot-sector start fields updated (NTFS/FAT hidden sectors incl. backups,
   exFAT PartitionOffset + boot checksum); partition entry rewritten by Windows (`IOCTL_DISK_SET_DRIVE_LAYOUT_EX`).
5. Unlock; engine verifies the new layout, other partitions unchanged, file system/label/letter (restoring the letter when MBR
   loses it).

## VM results (Windows Server 2025, i-012dadc6397d5add9, SYSTEM via SSM, production `WriteEngine` without IPC)

Each scenario used its own new 512 MiB VHDX (deleted afterwards) with 26–28 files (nested, up to 2 MiB, Unicode name,
read-only), and for NTFS an alternate data stream and a custom ACL.

| Scenario | Result | Notes |
|---|---|---|
| GPT NTFS, jump into separate free space | **PASS** | 4.3 s, 32 chunk checkpoints |
| GPT NTFS, overlapping move right (+16 MiB) | **PASS** | |
| GPT NTFS, overlapping move left (−64 MiB) | **PASS** | |
| GPT NTFS, 1 MiB shift (128 × 1 MiB chunks) | **PASS** | worst-case overlap |
| MBR NTFS, overlapping move right | **PASS** | drive letter kept |
| GPT FAT32, overlapping move right | **PASS** | read-only chkdsk exit 0 |
| GPT exFAT, overlapping move left | **PASS** | boot checksum recomputed; chkdsk exit 0 |
| GPT NTFS with a file held open | **PASS (refused)** | "volume is in use … Nothing was changed"; files unchanged |

Checks per moved scenario: engine OK; new offset; same size; drive letter; GPT partition GUID; every file's SHA-256, size and
attributes; NTFS ADS and ACL SDDL; volume serial; `Repair-Volume -Scan` NoErrorsFound (NTFS) or read-only `chkdsk` exit 0
(FAT32/exFAT); boot-sector start = new offset; journal ends `Verified`. The boot disk layout was unchanged after every run.

A first FAT32/exFAT run reported FAIL only because `Repair-Volume -Scan` does not support those file systems (harness
limitation; all Move checks passed); the rerun with read-only `chkdsk` passed. An earlier smoke run failed inside the test
script (a helper function shadowed `diskpart`) before any disk was created.

Evidence: `artifacts/cloud-test-control/reloc-smoke-2-result.json`, `reloc-all-1-result.json`, `reloc-fat-2-result.json`;
VM `C:\PMCloudTest-20261107\Reloc\` (probe output, journals, chkdsk logs). Unit/property tests: `BlockRelocationTests`
(randomised overlapping moves, interruption never overwrites uncopied source chunks, read-back corruption, boot sectors) and
`StagedEngineTests` move section. 409/409 non-VHDX tests pass.

## Limitations (honest)

- **Overlapping move interrupted mid-copy:** the original location is partly overwritten and the table still points to it.
  Per-chunk checkpoints make a resume possible (the copy order never overwrites uncopied source data — property-tested), but
  **automatic resume is not implemented**; recovery today is restore from backup/image. Non-overlapping moves leave the
  original intact until the table update.
- Not yet tested: through the app UI/IPC; process kill or OS restart mid-copy on real VHDX; 4Kn sector disks; very large
  partitions; MBR logical partitions (refused); BitLocker (refused); Move combined with resize in one task (simulated).
- Not a power-loss or device-failure test. Physical disks remain disabled.

## Multi-agent review (2026-11-08)

Three independent read-only reviewers (data safety, security, test gaps/UX) examined the Move path and helper changes.
Every finding was checked against the code before acting; fixes have regression tests (423/423 non-VHDX tests pass).

**Fixed**

| Finding (severity) | Fix |
|---|---|
| Failed/interrupted *overlapping* move released the lock; Windows could remount the half-moved volume and write over the only complete copy (critical) | Before the first overwriting write the partition gets a non-mountable "move in progress" type (GPT GUID `b7e1c6a0-…` / MBR `0x7F`), persistent across crashes; the final table update restores the original type |
| "Nothing was changed" inferred from message text; Win32 texts such as ERROR_BUSY ("in use") after writes would claim no change (high) | Typed `RelocationFailedException` carrying recorded progress; messages by phase: `MOVE_NOT_STARTED`, `MOVE_FAILED` (non-overlap, original intact), `MOVE_INTERRUPTED`, `MOVE_NOT_SWITCHED` (verified copy at destination), `MOVE_UNCERTAIN` |
| Disk number could be reassigned between validation and `\.\PhysicalDriveN` open; backend re-read the partition by number (medium) | Backend takes the validated `PartitionTarget`; on the opened handle: bus type FileBackedVirtual, exact entry (offset/size/GPT GUID), disk identity (GPT DiskId / MBR signature) captured and re-checked, destination free of every entry incl. extended containers, GPT usable range / MBR 32-bit limit — before the lock, inside the lock and again on the exact buffer passed to `SET_DRIVE_LAYOUT_EX` |
| Source misread detected only after overlapping data was gone (low-medium) | Per-chunk SHA-256 pre-pass; each chunk's source read is verified before it is written |
| Journal of a large move exceeded the 16 MiB inspection limit and blocked all later writes (medium) | Compact checkpoints (every chunk for the first 4096, then every 64th), `RelocationPlanned` record; 30 GiB/1 MiB test stays < 8 MiB |
| A "volume in use" refusal left a Failed journal that blocked every later operation (high, UX) | Terminal `NotStarted` written only when progress proves no write; still-blocking behaviour kept for any failure after a write |
| Journal checkpoint failure aborted an overlapping copy mid-way (medium) | Checkpoint write failures are counted and recorded, never abort the copy |
| Helper session limit 30 min (long moves reported "Lost contact") | 12 h |
| BitLocker refusal failed open for letterless volumes (WMI probe absent in helper) | Raw `-FVE-FS-` signature check under the lock |
| Second helper during a running move told the user to reinstall | Exit 70 "another helper is still running an operation" |
| Refusals titled "changes may be partially completed" | First-task refusal / `MOVE_NOT_STARTED` → "Nothing was changed" |
| Moves the helper refuses were routed as real | Eligibility mirrors helper refusals with a visible "will be simulated: …" reason |
| `COREHOST_*` not refused; `DOTNET_ROOT*` allow-listed | Both refused |
| Probe used a 30 s verify timeout (production 10 s) | Probe uses the production default |

**Open (recorded, not fixed in this pass)**

- No automatic resume / "finish table switch" recovery command for `MOVE_INTERRUPTED` / `MOVE_NOT_SWITCHED`.
- No per-chunk progress in the UI (dialog shows elapsed time only); success dialog does not show the helper's summary.
- "One structural change per disk per queue" is explained only at Execute time.
- Elevated GUI relaunch (Data Recovery) self-extracts WPF natives and logs to user-writable paths (security, high) — the GUI should never run elevated.
- Installer does not refuse a pre-planted junction at `%ProgramData%\PartitionManager` before icacls (helper fails closed at runtime).
- Drive-letter assignment is not restricted (letters referenced by machine configuration).
- Untested: through app/IPC/UAC; 4Kn disks; very large partitions on dynamic VHDX (host free space not checked); letterless partitions; MBR left moves and extended layouts; real interruption mid-copy.
