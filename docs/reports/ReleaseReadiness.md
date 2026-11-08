> Historical evidence through v0.3. For the freshly measured v0.4 results and current blockers, see [Milestone3.md](Milestone3.md).

# Release readiness: 0.3.0-alpha

**Verdict: NOT production-ready.** This is a prerelease (`0.3.0-alpha`). Version 1.0.0 isn't used because most
mandatory gates are open.

| # | Gate | Status | Evidence / blocker |
|---|---|---|---|
| 1 | Every feature has a documented status | PASS | [FEATURES.md](../FEATURES.md) |
| 2 | Enabled operations pass their relevant tests | **PARTIAL** | Label and letter: unit, IPC and failure-injection tests pass, and the elevated VHDX run 20261107-132147 passed with independent verification |
| 3 | No known critical data-integrity defects | PASS (none known) | D1–D6 in PRODUCTION_AUDIT.md fixed |
| 4 | Privileged IPC passes security review | **FAIL** | Internal review only; residual risks open; no independent review |
| 5 | Virtual-disk integration tests pass | PARTIAL | Run 20261107-132147 passed all steps (label and letter ops on GPT and MBR VHDX, stale-layout and Disk 0 refusals, detach, identity-checked deletion). Not yet covered: the app → UAC → pipe → helper path, and structural operations (not implemented). |
| 6 | Failure-injection tests pass | PARTIAL | Fake-backend injection passes; device-level injection on VHDX not run |
| 7 | UI automation passes | PASS | 14/14 |
| 8 | Installer and uninstaller pass | **FAIL (not run)** | Installer builds; install and uninstall not exercised |
| 9 | Accessibility testing complete | **FAIL** | Automation names exist; no screen-reader or high-DPI audit |
| 10 | Documentation matches behaviour | PASS | Updated this milestone |
| 11 | Reproducible artifacts | PARTIAL | Two consecutive Release builds on this machine produced byte-identical `PartitionManager.exe` and `PartitionManager.Helper.exe` (same SHA-256). The installer hash differs between builds (embedded README and timestamps). Not yet compared across machines. |
| 12 | No unexpected changes to protected system partitions | PASS | No write path to physical disks exists; safety policy tests |
| 13 | Physical-disk write validation | **NOT STARTED** | Disabled by design. The E:-only scope exists only as a design ([E_ONLY_AUTHORIZATION_DESIGN.md](../E_ONLY_AUTHORIZATION_DESIGN.md)) and needs your separate explicit authorization |
| 14 | Limitations disclosed | PASS | README, FEATURES.md, this report |
| – | Code signing | **FAIL** | No certificate available; binaries are unsigned |

## Artifacts (built 2026-11-07)

See `artifacts/checksums/SHA256SUMS.txt` for hashes.

| File | Size | Signed |
|---|---|---|
| `artifacts/publish/PartitionManager.exe` | 68.6 MB | No |
| `artifacts/publish/PartitionManager.Helper.exe` | 33.8 MB | No |
| `artifacts/installer/PartitionManager-0.3.0-alpha-x64-setup.exe` | 91.2 MB | No |

The SBOM is `artifacts/reports/sbom.json` (NuGet dependency graph) and the vulnerability scan is
`artifacts/reports/vulnerabilities.txt`.

## 0.3.0-alpha changes

Added real create, format, delete, shrink, extend and split for **approved virtual disks only**, tested on GPT and MBR VHDX with independent verification and 14/14 data-integrity checks. Gates 2 and 5 improve, but gates 4, 8, 9, 13 and code signing remain open. See TestResults.md, DataIntegrity.md and SecurityAudit.md.
