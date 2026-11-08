# UI and accessibility: 0.4.0-alpha

Implemented: operation-preview accessible name; real progress explicitly says
"Real operation on virtual disk"; real success says "Completed and verified";
failure says changes may be partially completed and instructs refresh/inspection.
Simulation retains its explicit no-write message and simulated-layout banner.
Move/Merge routing explains unsupported execution; unapproved initialization is
blocked and simulated with a reason. Physical disks remain blocked by the helper.

Tested: 16/16 owned-process UI Automation smoke checks on the Release WPF build,
in demo mode, including confirmation, simulated execution, error prevention,
disk-map changes and recovery-page navigation. Evidence: artifacts/reports/m3-ui.
The existing script also captures light and dark windows.

Not tested: Narrator/NVDA reading order, complete keyboard-only operation,
150%/200% DPI, high-contrast themes, real-operation progress over an installed
helper, actual partial failure through the UI. No full accessibility compliance
claim. UI smoke does not replace assistive-technology testing.
