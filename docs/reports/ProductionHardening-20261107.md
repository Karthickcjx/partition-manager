# Production hardening follow-up — 2026-11-07

**Real VHDX regression passed; public physical-disk release remains blocked.** User requested continued production work and allowed resource downloads. No physical E: operation was performed or enabled.

Elevated run 15:55–15:57 IST: **7 passed, 0 failed**. Evidence retained as `artifacts/reports/m4-vhd-validated.trx` and `.log`.

- Real GPT/MBR initialization and metadata operations passed.
- New journal integration passed on GPT and MBR: initialize, create/format, label; independent volume query; three valid terminal Verified journals per disk.
- Structural operations passed with independent layout/file checks (`StructuralRun-20261107-155515.md`).
- Large sparse image: 16 GiB virtual capacity, 8 GiB NTFS shrunk to 4 GiB; all 1003 SHA-256 file hashes matched, including a 128 MiB file, Unicode, read-only and long paths.
- Protected-region rehearsal passed on the adjusted synthetic replica. Its extents have a uniform 32 MiB shift, provider rounding and BasicData types with model role annotations. This is **not** an exact physical-layout or boot/recovery rehearsal.
- Disposable images detached/deleted by their fixtures; test root removed after becoming empty.

Final read-only host partition comparison against retained `docs/reports/E_ONLY_LAYOUT_0.5.json` matches identifiers, partition types, boundaries and boot/system flags. See `m4-post-vhd-host.json`. E: was independently rediscovered before testing at GUID `9be001de-fec0-4988-adb6-2508a3d49b9d`, start 451977150464, length 59860058112 on disk unique ID `eui.001B448B4A510562`. This is not a full E: data hash.

The unelevated runner remained waiting after successful completion because `Start-Process -Wait` can include persistent build descendants. After verifying the completed TRX/transcript and absence of test images, only the owned waiting wrapper was stopped. The script now waits on the elevated runner process itself with `WaitForExit`; script syntax checked. The successful test result is taken from TRX, not the interrupted wrapper's exit status.

No additional downloads were necessary for these tests. User reports no test machine. Read-only inventory found Windows 11 Home Single Language, approximately 24 GiB RAM, an active hypervisor, and no installed Windows Sandbox or Hyper-V management executable. No hypervisor was installed or reboot requested. Full installer/installed-helper and destructive crash-recovery testing still need a disposable Windows VM or separate test machine.

Remaining public-release gates: verified backup/restore and recovery media before physical testing; protected physical authorization implementation and independent review; installed helper/installer security and lifecycle validation; real interruption/device-failure recovery; manual accessibility matrix; controlled physical validation; signing. Move/Merge remain simulated. Passing these seven tests does not enable physical writes or establish production readiness.

These results supersede the canceled-VHDX-run status in earlier Milestone 4 reports. Existing packaged binaries were not rebuilt: this follow-up changes the runner and evidence, not the application binaries.
