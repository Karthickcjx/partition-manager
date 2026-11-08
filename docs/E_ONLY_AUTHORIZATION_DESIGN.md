# E:-only authorization: design and rehearsal, 0.4.0-alpha

> v0.5 planning update: E: is now designated for testing, but physical writes
> remain disabled. The [dedicated physical-test design](E_ONLY_PHYSICAL_TEST_AUTHORIZATION.md)
> and [development plan](V0.5.0_DEVELOPMENT_PLAN.md) supersede future-work policy
> in this document. The implementation and rehearsal evidence below remain the
> historical v0.4 baseline, not physical-write authorization.

**No physical writes are enabled.** The helper always rejects physical disks.
There is no physical-write switch, registry option, environment variable, compile
flag or grant-creation command. Earlier proposals for such activation mechanisms
are superseded by this milestone's design-only scope.

## Read-only identity inspection

`Core/Safety/EScopeRehearsal.cs` consumes discovery snapshots, never raw write
handles. It resolves E: to exactly one GPT data partition, requires stable disk
and partition identities, rejects duplicate GUIDs/ambiguous disks, checks extents
for overlap and overflow, and records every other partition as protected.
The live inspection test emits `e-scope-proposal-design-only.json` when a report
folder is supplied. This JSON is evidence, not an executable authorization token.

Observed geometry on 2026-11-07 (not permanent identifiers): Disk 0, E: partition
7, offset 451,977,150,464, size 59,860,058,112 bytes. The policy binds disk UniqueId,
serial, capacity, layout fingerprint and the actual partition GUID/extent, not
just these numbers or E:. Metadata fingerprint also includes roles, GPT types,
hidden/active/boot/system flags, volume paths and encryption state. Protected set:
C:, D:, EFI, MSR and both recovery partitions.

## In-memory authorization rehearsal

Only a hypothetical shrink with fixed start is modeled. The full proposed layout
must preserve every protected partition. A fresh confirmation nonce binds to the
policy and proposed layout hash; it expires after two minutes and is consumed on
every attempt, including rejection. Reusing a nonce, changing the layout, moving
E:'s start or modifying a protected entry fails. No successful rehearsal result
can reach WriteEngine or produce permission to write a physical disk.

Confirmation records are deliberately in-memory, unsigned test data. They do not
implement authenticated human consent, persistent replay protection or secure
policy issuance. No security claim is made for exporting/importing them.

## Layout rehearsal

Unit fixtures reproduce the seven measured extents using generated identifiers.
The VHDX integration fixture obtains fresh host metadata read-only, creates a
new sparse VHDX and reproduces partition geometry using synthetic BasicData
entries. Windows requires reserved GPT space at the front, so replica entries
are shifted uniformly by 32 MiB and the virtual capacity grows by 64 MiB. Any
Windows-created MSR is preserved. No host partition is copied, formatted, mounted
or otherwise changed. Host roles and C/D/E letters are annotations in memory;
real C:, D: and E: letters are never assigned to the replica. Windows rounds the synthetic C: size up by 713,216 bytes; D: and E: sizes match exactly. The three corresponding data partitions are formatted and seeded with synthetic files, hash-checked using temporary G-Z letters. Proposed deletions
of all protected entries must be refused. This is authorization rehearsal, not
proof that future physical storage operations are safe.

## Threat model and remaining requirements

| Threat | Current treatment | Future physical-write prerequisite |
|---|---|---|
| Drive-letter/disk-number reassignment | Stable identity and geometry matching; ambiguity rejected | Independent native device identity and raw GPT disk GUID |
| Layout races | Compare freshly discovered layout and metadata | Exclusive access strategy; revalidate at every commitment point |
| Protected entry tampering | Full protected snapshot equality in rehearsal | Byte-level primary/backup GPT entries and CRC verification |
| Confirmation replay or altered proposal | Nonce, age and proposal-hash checks in memory | Authenticated human confirmation; protected durable replay store |
| Malicious editable policy | Evidence JSON is never executed | Authenticated policy issuance in an independently reviewed design |
| Hidden encryption/dirty filesystem | No physical execution capability | Fail closed on unknown BitLocker state, dirty FS or pagefile use |
| Disk-wide GPT failure/power loss | No physical execution capability | Full verified backup, recovery media and controlled VM interruption tests |

Changing E:'s partition entry rewrites shared disk-wide GPT structures. Extent
restrictions cannot isolate C:, D:, EFI, MSR or recovery from that risk. Neither
atomicity nor safe reversibility is claimed. Raw GPT primary/backup snapshots,
AC-power checks, protected entry byte comparison, BCD verification, secure grant
storage, recovery journaling and independent review are **not implemented**.

A future physical-write milestone requires separate explicit authorization and
all prerequisites above. This milestone provides no such authorization.
