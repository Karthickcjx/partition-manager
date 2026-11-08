# Physical E: test readiness — v0.5 planning update

**Blocked. No physical operation authorized or enabled.** This is an initial
readiness inventory, not certification that phases 1–4 have completed.

| Gate | Status | Evidence / missing work |
|---|---|---|
| User designation | Recorded | E: dedicated to testing; not exact-operation consent |
| Fresh read-only discovery | Verified for planning | [Snapshot](E_ONLY_LAYOUT_0.5.json); target GUID `9be001de-fec0-4988-adb6-2508a3d49b9d`, original extent [451977150464, 511837208576) |
| Raw GPT/native identity enrollment | Pending | Disk GUID, both tables/CRCs and reviewed immutable enrollment |
| Policy implementation on VHDX | Partial, historical | Existing shrink rehearsal is in-memory and cannot authorize physical writes; durable confirmation, provenance and enforcement remain planned |
| Protected-partition security tests | Partial, historical | v0.4 negative tests and [adjusted VHDX rehearsal](runs/m3-layout-rehearsal.md); must repeat/expand against v0.5 implementation |
| Complete Disk 0 backup verified | Not verified | No backup artifact, coverage/verification log or independent storage evidence supplied/validated |
| Tested recovery media and restore | Not verified | No boot/restore drill evidence supplied/validated; no host restore attempted |
| Independent implementation review | Pending | Prior internal review is not independent physical-authorization approval |
| Exact operation and whole-disk after-layout | Not selected | No size, operation or parameters approved; current full layout in design |
| Exact physical consent | Not requested | Wait until readiness evidence and concrete operation are reviewable |

The fresh snapshot records six protected partitions alongside E:, on the same
512,110,190,592-byte GPT device. Current numbers and drive letters are locators,
not authorization. The design binds stable identities and immutable original
boundaries and refuses changes to every other partition.

No physical tests, backup restore, E: data writes or application installation
were performed for this planning update. No v0.5 executable was released. Existing
v0.4 test counts are historical, not freshly re-run results for v0.5. The physical
gate remains unconditional in `WriteEngine.Execute`; the E-scope rehearsal is
not consulted by that gate.

Next development work: phase 1 VHDX-only enforcement and phase 2 negative tests.
Phase 3 needs identified backup/recovery artifacts and a safe isolated restore
target. Update this report with evidence tied to the exact build and operation,
then stop for specific authorization as required by the user's policy. If scope
confinement cannot be proven, report refusal instead of advancing to execution.
