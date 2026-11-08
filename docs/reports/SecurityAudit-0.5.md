# Security audit — 0.5.0-preview

Update 2026-11-07: see [SecurityHardening-0.5.1](SecurityHardening-0.5.1.md) and [ReleaseGate-0.5.1](ReleaseGate-0.5.1.md) for installed-VM results and the current gate.

Engineering self-review, **not independent security approval**. Physical writes remain rejected in the helper regardless of UI, drive letter or region-model result. Move/Merge remain blocked for real execution. Existing controls include authenticated named-pipe session checks, operation allowlisting and identity validation, native backing-image pinning, protected installation/audit-directory checks and no development override for the shipped elevated helper.

New controls: durable intent before mutating calls; exclusive journal session; reparse-point and protected-directory validation; corruption/interruption fail-closed handling; repeat identity/layout validation at mutation checkpoints; one-use initialization history; full confirmation layouts. Region-model tests exercise all protected partitions, out-of-region growth, physical enrollment, target-letter changes, renumbering, stale and replayed confirmation and changed proposal hashes. These tests do not grant physical permission.

| Risk / boundary | Status and release impact |
|---|---|
| Physical enrollment/provenance, administrator reauthentication and durable exact-operation consent | Not implemented; physical writes stay disabled |
| Backup, recovery-media and restore evidence | Unverified; physical readiness fails |
| Independent review | Pending; author self-review is insufficient |
| External administrator changes between checks and native writes | Not eliminated; checks reduce but do not prove absence of races |
| Journal deletion/replacement by administrator | Hash chain is not privileged-tamper protection; no such guarantee |
| Interrupted/failed operation resolution | Conservative global block; no supported resolution workflow yet |
| Installed helper ACL, UAC rejection, DLL/extraction and standard-user replacement attacks | Installed VM tests run for 0.5.1-preview ([SecurityHardening-0.5.1](SecurityHardening-0.5.1.md)): ACL, planting, IPC and squat checks pass; startup-hook execution fixed; **CoreCLR profiler variables still load a DLL into the elevated helper (release blocker)**; interactive UAC not tested |
| Actual helper termination, device disconnect and power failure while mutating | Not established by file-journal fault tests |
| Move/Merge preservation of filesystem semantics | Unproven; real operations refused |
| Unknown E: encryption | Fail-closed for future physical readiness |
| Unsigned release executables | Preview distribution only; no signing certificate available |

Threat model: an unprivileged client may forge requests, swap targets, replay consent, modify writable files, or interrupt IPC. Elevation must not turn client-provided drive letters/paths into authority. Administrator compromise is outside the current tamper-resistance claim. Existing IPC, policy and path tests remain part of ordinary regression. No security product or reference installation was modified.
