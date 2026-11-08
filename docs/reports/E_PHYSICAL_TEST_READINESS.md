# E: physical-test readiness — 0.5.0-preview

**NOT READY. Physical writes remain unconditionally disabled. No physical operation is authorized by this report.** Captured read-only on 2026-11-07; machine-readable evidence: `artifacts/reports/m4-before.json`. Drive letters and disk numbers are observations, never authority.

The discovered GPT NVMe disk is 512110190592 bytes, unique ID `eui.001B448B4A510562`, serial `E823_8FA6_BF53_0001_001B_448B_4A51_0562.`, currently Disk 0. Logical/physical sectors: 512 bytes. Windows reports Healthy, boot and system disk. E: is Basic Data GUID `9be001de-fec0-4988-adb6-2508a3d49b9d`, currently partition 7. Its immutable proposed scope is **[451977150464, 511837208576)** bytes, length 59860058112. NTFS, blank label, volume size 59860054016, available 59769393152 at capture, Healthy, not boot/system. BitLocker query failed: **Unknown**, not evidence of no encryption.

| Existing partition | Start byte | Length bytes | Treatment |
|---|---:|---:|---|
| EFI | 1048576 | 272629760 | Protected |
| MSR | 273678336 | 16777216 | Protected |
| C: | 290455552 | 160660004352 | Protected |
| Recovery | 160951173120 | 1062207488 | Protected |
| D: | 162014429184 | 289962721280 | Protected |
| E: | 451977150464 | 59860058112 | Design scope only; writes disabled |
| Recovery | 511837208576 | 272629760 | Protected |

Final read-only inventory (`artifacts/reports/m4-after.json`) matches the initial partition identifiers, types, offsets and lengths, and E: filesystem, size, available bytes and health. No physical disk operation was requested or issued. This is layout evidence, not a byte-for-byte verification of all E: data.

| Mandatory prerequisite | Result | Evidence / missing work |
|---|---|---|
| Complete identified Disk 0 backup verified | FAIL — unverified | No backup evidence supplied or independently verified |
| Tested bootable recovery media | FAIL — unverified | No boot test evidence |
| Successful restore rehearsal | FAIL — unverified | No restore to a separate disposable target, layout/file/boot verification |
| Fresh physical layout capture | PASS | Read-only inventory above; must repeat immediately before any future operation |
| Layout-matched VHDX rehearsal of exact implementation | PENDING | Follow-up region/journal VHDX tests passed; replica uses shifted extents, rounding and annotated roles, so exact physical-layout rehearsal remains pending |
| Independent physical authorization review | PENDING | No independent reviewer; current implementation is virtual-only |
| Installed helper and installer security validation | PENDING | No isolated Windows VM; harness supplied |
| Explicit exact-operation authorization | NOT GRANTED | No operation-specific approval requested; prerequisites unmet |

`RegionRehearsal` enrolls virtual GPT disks only, preserves immutable bounds and every other partition, tracks children within the region, rejects stale or replayed confirmations and changed snapshots. It is an executable **model**, not physical authorization. In-memory confirmations are not authenticated durable physical permissions.

Before physical execution could even be proposed: complete all prerequisites; implement protected enrollment provenance, administrator authentication, durable single-use operation consent binding the complete before/after layouts and exact target; revalidate identities, encryption state and raw layout immediately before every write; prove confinement including metadata writes. Partition-table changes touch disk metadata outside E:'s data extent, so simple byte-range checks are insufficient. Unknown safety state, changed layout, missing evidence or unprovable confinement must refuse execution. Never initialize/erase the host disk or modify any protected partition.

The first specific physical operation must be proposed only after readiness passes, with full disk layouts, exact identifiers/boundaries, effects, recovery evidence and verification procedure. Stop for explicit user authorization then. This milestone grants none.
