# Partition Manager 0.6.0-preview

> **This release makes REAL changes to your disks. There is no simulation mode.** Back up everything first and keep
> Windows recovery media at hand. Unsigned preview, no independent security review, use at your own risk.

## What's new

- **No more simulation.** Confirmed tasks are written to the real disk. Tasks that cannot run for real (Merge, a move
  combined with a resize, a second structural change on the same disk in one run) are refused with a reason instead of
  being rehearsed.
- **All disks.** The installer sets the helper policy to `AllDisks`. Built-in limits remain: the running Windows, EFI,
  MSR and recovery partitions can only be shrunk or extended in place; Initialize only on blank disks.
- **Real block-level Move** with per-chunk verification and full SHA-256 comparison; volume serial, partition GUID,
  ACLs and alternate streams preserved; a "move in progress" marker protects an interrupted overlapping move.
- **Data Recovery through the helper.** The app never runs as administrator; recovered files are written by you.
- Security hardening: startup hooks disabled, runtime-variable guard, protected ProgramData folder, no native
  self-extraction for the helper, app launched unelevated after setup.
- Fixes: misleading "Simulation mode" header, Data Recovery progress crash, "another helper is running" false alarm,
  accurate "Nothing was changed" dialogs.

## Verified

- 437 automated tests pass (unit, IPC/security, failure injection, property tests of the Move copy order).
- On Windows Server 2025 (disposable EC2 VM): real Move scenarios on throwaway VHDX (8/8), C: shrink, standard-user
  security suite 111/0 (on the 0.5.1 build).

## Not verified / known issues

- **This exact 0.6.0 build was not install-tested on the VM** (the VM was stopped). The AllDisks policy path is covered
  by unit tests only.
- Not tested on Windows 10/11 physical PCs.
- Unfixed security issue F4: .NET profiler environment variables reach the elevated helper.
- Interrupted overlapping Move has no automatic resume (restore from backup).
- No live progress during a long Move. Merge, Clone, Convert, Backup and Toolkit are not available.
- Unsigned: Windows SmartScreen and UAC show "Unknown publisher".

## Download

`PartitionManager-0.6.0-preview-x64-setup.exe`
SHA-256 `9f78185b89a30b40ab4d0f0f7e8af11453bae66899c6cdb1e2194424f44abe3c`
