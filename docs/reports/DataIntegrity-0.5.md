# Data integrity — 0.5.0-preview

Update: the [follow-up run](ProductionHardening-20261107.md) passed all 7 elevated VHDX tests and verified 1003 hashes. It supersedes the pending-run status below; physical data integrity is not claimed.

No physical-disk mutation or E: filesystem operation was issued in this milestone. Host inventory is read-only. Layout comparison cannot prove every E: data byte unchanged; no such claim is made.

The prior v0.4 VHDX evidence (1003 matching hashes, 14 structural checkpoints, GPT/MBR and protected-region rehearsal) remains historical evidence, linked from [Milestone 3](Milestone3.md). It must not be relabeled as a successful run of the new journal implementation.

The v0.5 suite adds actual disposable-VHDX journal coverage for GPT/MBR initialization, creation and labeling, plus region-model validation in the layout-matched fixture. **Current run pending:** Windows elevation was canceled before these integration tests started. No new VHDX success or hash result is asserted. The previous `vhd-run.trx`/log retain their old timestamps and are not v0.5 evidence.

Ordinary tests exercise identity, policy, boundary, stale-confirmation, journal and failure semantics with models/fake backends. Move/Merge remain simulated because filesystem preservation is unproven. No fault-injection result is a substitute for independently checking actual files, both partition tables, protected entries and free-space boundaries after real VHDX operations.
