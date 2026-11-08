# Dedicated E:-only physical test authorization design — v0.5

**Design, not an enabled capability.** The helper's physical-disk rejection remains
unchanged. E: is designated for testing, but no exact physical operation has been
authorized. Current code implements only the earlier in-memory shrink rehearsal;
the requirements below are planned enforcement and must be independently reviewed.

## Rediscovered candidate and immutable scope

Fresh read-only Get-Disk/Get-Partition discovery is saved in
[E_ONLY_LAYOUT_0.5.json](reports/E_ONLY_LAYOUT_0.5.json), with capture time and all
seven partitions. Discovery matched the candidate by its current E: mount, then
recorded stable IDs; E: is only a discovery hint and display label.

- Disk UniqueId: `eui.001B448B4A510562`.
- Disk serial: `E823_8FA6_BF53_0001_001B_448B_4A51_0562.`
- Capacity: 512,110,190,592 bytes; GPT; currently Disk 0.
- Target GPT partition GUID: `9be001de-fec0-4988-adb6-2508a3d49b9d`.
- Target type: `ebd0a0a2-b9e5-4433-87c0-68b6b72699c7` (BasicData).
- Original extent: **[451,977,150,464, 511,837,208,576)** bytes.
- Original size: **59,860,058,112 bytes**, approximately 55.75 GiB; currently #7.

These observed values are not a grant. Enrollment must also validate native
device identity, volume identity, sector sizes, raw GPT disk GUID, both GPT copies
and CRCs. Missing or ambiguous identifiers refuse enrollment. Raw GPT identity
has not been collected by this planning update. Persist the original extent in
a protected immutable enrollment record; refreshing, shrinking, rebooting or
changing letters must never widen/reset it. Any changed enrollment needs a new
review and explicit authorization, not automatic rebinding to the current E:.

## Complete current layout and required preview

| Current # / label | Start bytes | Size bytes | Protection |
|---|---:|---:|---|
| 1 / EFI | 1,048,576 | 272,629,760 | Protected |
| 2 / MSR | 273,678,336 | 16,777,216 | Protected |
| 3 / C: | 290,455,552 | 160,660,004,352 | Protected |
| 4 / Recovery | 160,951,173,120 | 1,062,207,488 | Protected |
| 5 / D: | 162,014,429,184 | 289,962,721,280 | Protected |
| 7 / E: | 451,977,150,464 | 59,860,058,112 | Candidate scope only |
| 6 / Recovery | 511,837,208,576 | 272,629,760 | Protected |

Every operation preview must display this whole disk in spatial order, including
unallocated gaps and disk metadata reservations, with before/after start, end,
size, partition/type GUID, flags, role and volume identity. Label all protected
rows unchanged and show the precise E: change. No after-layout is approved yet;
do not invent sizes or imply a specific shrink has been selected.

## Helper-side policy and execution contract

1. **Separate policy from client input.** A protected enrollment record binds
   native disk ID, GPT disk GUID, serial, size, sectors, target GUID/type/volume,
   immutable original extent and complete protected partition snapshots. The
   helper must not trust client-provided policy, free-space claims or identity.
   Protect policy/confirmation storage ownership, ACLs and integrity; fail closed
   on tampering, stale state or missing evidence.
2. **Deny by default.** Initially rehearse fixed-start shrink only. No generic
   disk operation is authorized. Disk initialization/erase, protected partition
   operations, moving the target start and unsupported migrations are forbidden.
   Any later allowed kind requires its own design, tests, review and exact consent.
3. **Boundaries.** Checked arithmetic must prove all target extents remain inside
   the immutable half-open original interval. Enforce alignment and filesystem
   limits independently. Reject underflow, overflow, negative/zero sizes,
   overlap and protected entry addition/deletion/attribute changes. Preserve
   every other original entry by GUID, full geometry, type and metadata.
4. **Administrator authentication plus human consent.** An elevated helper with
   authenticated IPC must validate the administrator session; UAC authentication
   is necessary but insufficient. A fresh interactive confirmation shows the
   complete preview and destructive effects. Bind a short-lived one-use nonce
   to exact operation/parameters, target IDs, original scope, current raw-layout
   digest, proposed-layout digest, build identity and prerequisite evidence IDs.
   Consume durably before dispatch, including uncertain outcomes; restart or
   reconnect must not replay it. UI refresh, parameter or layout changes revoke
   it. No reusable blanket permission for E:.
5. **Just-in-time revalidation.** Immediately before the first mutating call,
   independently rediscover all identities, volume state and both GPT copies;
   compare against the confirmed complete layout and protected snapshot. Reject
   renumbering, replacement, ambiguity, mount redirection or any layout change.
   Validate locks, encryption, dirty state and supported sizes. Prove a suitable
   exclusion strategy against concurrent layout changes; repeat checks at every
   mutation checkpoint. If the check-to-write race cannot be bounded sufficiently
   for the reviewed scope, refuse physical execution.
6. **Partial failure.** Stop subsequent work on any mismatch or uncertain API
   result. Do not claim rollback/unchanged state and do not retry a destructive
   call automatically. Re-enumerate safely, retain evidence, verify survivors
   and provide the reviewed recovery procedure. Do not kill an active storage
   call to simulate cancellation or power loss on the host.
7. **Verify and audit.** Persist intent and prerequisite/confirmation digests
   before execution. Record request/build/admin identity, every step, native
   status, actual before/after table digests, protected entry comparison, file
   integrity results and final verified/partial/unverified status. Validate both
   GPT copies and filesystem/layout results independently. Refuse before writing
   if durable audit cannot start; an audit failure after mutation is unverified,
   stops the queue and cannot be presented as success. No silent history removal.

## Disk-wide risk and refusal rule

An E:-extent policy cannot guarantee that only E:'s physical sectors are written:
partition operations update shared GPT metadata describing the entire disk.
Preserving all other entries is mandatory, but does not eliminate disk-wide
failure risk. The review must explicitly distinguish filesystem/data confinement
from necessary shared-table writes and assess the exact operation. If the user's
approved scope or the implementation cannot accommodate these effects reliably,
**refuse execution**, even after backup and consent. No claim of atomicity or safe
reversibility is made.

## Mandatory release gates and authorization stop

All gates must pass together: verified complete Disk 0 backup on separate media;
successful restore using tested recovery media on a disposable target; fresh
layout-matched VHDX rehearsal of the exact proposed behavior; independent review
of the exact physical authorization implementation with findings resolved; and
explicit authorization for the exact operation and stable target. Review and
backup freshness must be invalidated by material changes. The first four gates
must be documented before requesting the fifth. No automatic physical-write
enablement, registry/environment/CLI bypass, Disk 0 initialization or erasure.

See the [five-phase plan](V0.5.0_DEVELOPMENT_PLAN.md) and
[current blocked readiness](reports/E_ONLY_TEST_READINESS_0.5.md).
