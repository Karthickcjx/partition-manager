# Engineering audit — 0.5.0-preview

Baseline inspected: solution/projects, core operations and identity policy, helper validation/execution/IPC, native backing-image lease, audit/install guards, WPF dialogs/theme/view model, integration tests, installer, release tooling, and v0.4 reports. Fresh pre-change ordinary regression: **336 passed, 0 failed** (`m4-baseline.trx`). SDK uses the existing user-local .NET installation.

The existing virtual-disk create/format/delete/shrink/extend/split and initialization implementations were preserved. No new data-moving algorithm was enabled. New code concentrates on durable operation intent, interruption refusal, scoped virtual rehearsal, complete confirmation layouts and conservative UI defaults. The unsafe staging prototype remains unreachable in real execution; see [Move/Merge limitations](../MOVE_MERGE_LIMITATIONS.md).

Key changed areas: `Core/Safety/RegionRehearsal.cs`, `Core/Layout/LayoutPreviewRows.cs`, `PrivilegedHelper/Engine/OperationJournal.cs`, helper engine/startup, desktop confirmation/theme/dialog code, production tests, the non-storage `tools/JournalCrashProbe`, VM/journal inspection scripts and preview version/release metadata. No host installation or physical authorization was added.

Release disposition: **preview engineering build; mandatory virtual-disk/recovery/security prerequisites remain open**. Read the security, recovery, installer, accessibility and E: readiness reports for explicit limits. No independent review or production-readiness claim.
