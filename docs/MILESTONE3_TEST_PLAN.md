# Milestone 3 safety plan and fresh baseline

2026-11-07. Work continues from the existing working tree; it is largely untracked.
Baseline: user-local .NET 8 SDK, 301 passing and 1 failing non-destructive test
(`Merge_nonadjacent_keeps_freed_space_unallocated`); 302 total. System dotnet has
no SDK. Baseline TRX: artifacts/reports/m3-baseline.trx.

The inherited code already includes experimental Move/Merge and initialization,
despite documentation describing them as simulated. Move/Merge must be blocked
in the shipped helper until consistency, metadata fidelity and durable recovery
are proven. Existing experimental tests are development evidence only.

1. Run ordinary tests with VirtualDisk and RealVolumeRecovery excluded. Never
   enable the legacy physical-volume recovery test. Record host layout read-only.
2. Restrict every image factory to D:\PartitionManager-TestDisks, reject reparse
   ancestors, existing filenames, unexpected size and insufficient free space.
   Small tests use 64 MiB to 2 GiB dynamic VHDX; the large-volume test uses 16 GiB; geometry rehearsal may use a
   512,110,190,592-byte dynamic VHDX with only small synthetic payloads.
   Preflight measured 130.78 GiB free. Require 8 GiB reserve before small tests;
   a sparse geometry rehearsal must not fill the virtual disk.
3. Run helper initialization on newly created identity-bound approved images,
   separately GPT/MBR. Independently query Windows after each operation.
4. Re-run label/letter and structural integration with SHA-256 checks, partial
   split failure and recovery. No C:, D:, E: volume is a write target.
5. Add fail-closed tests for unsupported operations, image aliases, approval
   mismatch/replay, protected partitions and design-only E-scope proposals.
6. Exercise synthetic Unicode/deep/long/read-only/many/large files with bounded
   buffers; report real-media interruption and VM power-loss tests as pending.
7. Build WPF, helper, installer; run owned-process UI automation. Generate hashes,
   dependency inventory and security/accessibility/installer reports.
8. Compare host layout with the read-only snapshot. E: content is not read or
   written; unchanged layout is not a claim of full-volume byte comparison.

No physical write exception, no original 4DDiG changes, no host installation or
uninstallation. VM installer lifecycle, real UAC/pipe integration, screen reader,
DPI and power-loss validation remain pending unless an isolated VM is available.
Critical unresolved security findings block release; unsigned artifacts are for
review and testing, not production deployment.
