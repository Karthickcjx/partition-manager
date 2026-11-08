# Security hardening — 0.5.1-preview (2026-11-07)

This is an engineering self-test on one disposable AWS VM. It is **not** an independent security review and it does not establish production readiness. Physical-disk writes stay disabled, and real Move/Merge stays refused. No host partition (C:, D:, E:, EFI, MSR, recovery) was touched. The development host had nothing installed on it.

## Environment

- **AWS account and region:** caller identity verified before acting; region ap-south-1.
- **Instance:** i-012dadc6397d5add9, m7i-flex.large, Windows Server 2025. Every VM script refuses to run unless IMDSv2 returns this instance ID and `-DisposableVmConfirmed` is passed.
- **Access:** SSM only (`AWS-RunPowerShellScript`), so all automated checks ran as **SYSTEM**.
- **Standard-user checks:** these used a temporary local account, `pmstd<random>`. It was in Users only, had a random password that was never output, expired after 6 hours, and was deleted (account and profile) at the end of each run.
- **Installed app:** upgraded 0.5.0-preview → 0.5.1-preview at `C:\Program Files\PartitionManager`, then reinstalled in place with the rebuilt 0.5.1 (helper environment guard).
- **Installer transfer:** each time through a temporary private S3 bucket with Block Public Access, using a 600 s presigned URL. The URL was never printed or stored locally. The VM checked the SHA-256 before running the installer. Both buckets were deleted and confirmed absent.

## Findings and fixes

| # | Finding on installed 0.5.0-preview | Severity (self-assessed) | 0.5.1 change | Re-test on installed 0.5.1 |
|---|---|---|---|---|
| F1 | **The elevated helper ran a `DOTNET_STARTUP_HOOKS` assembly from its environment.** The probe marker was written from inside the helper. | High: environment control turns a UAC approval into arbitrary code running elevated | `StartupHookSupport=false` for helper and app. Static regression test checks the generated helper runtimeconfig. | **PASS**: no marker. Exit 64 before the guard was added, 69 after. |
| F2 | **A standard user could create `ProgramData\PartitionManager\Audit` and a file in it before the helper's first run.** The helper then refused with exit 68 (fail-closed, but a denial of service). | Medium (availability; no write bypass) | Installer `ssInstall` step: owner set to Administrators recursively, protected ACL (SYSTEM/Admins full, Users read/execute), children reset to inherit. Any non-zero icacls result aborts the install. | **PASS** after repair (owner Administrators; squatter write denied; Users cannot create in root; helper serves). The pre-install squat itself still succeeds (row stays **FAIL**), because Windows lets Users create new ProgramData folders. Install or reinstall is the repair; the uninstaller keeps this folder. |
| F3 | **Helper published with native self-extraction enabled.** No extraction occurred in tests, but it is a temp-directory DLL-loading risk. | Low/defence in depth | Helper is published with `IncludeNativeLibrariesForSelfExtract=false` (csproj and build script). | **PASS**: 28 helper modules, all under Windows or the install folder. |
| F4 | **CoreCLR profiler variables make the elevated helper map a DLL from a user-writable folder.** The variables were `CORECLR_ENABLE_PROFILING`/`CORECLR_PROFILER`/`CORECLR_PROFILER_PATH[_64]`. The DLL was mapped with Execute access, so DllMain would run elevated. | **High, release blocker, NOT FIXED** | Defence in depth only: `RuntimeEnvironmentGuard` exits 69 when `DOTNET_*`/`COMPlus_*`/`CORECLR_*`/`COR_*` variables are present (SDK-only names allowed; values never logged). This runs **after** the runtime has acted, so it cannot prevent the load. | **FAIL**: 4663 Execute/Traverse by `PartitionManager.Helper.exe` on the probe DLL; exit 69. |

F4 detection method:
- A copy of the signed `version.dll`, renamed and placed in `C:\Users\Public\…`, had a temporary file-system audit SACL.
- The audit policy was restored to "No Auditing" afterwards, and the probe folder was removed.
- Two positive controls confirmed the detector:
  - `powershell.exe` with `COR_*` logged event 1022 with HRESULT 0x8007007F (export missing, so LoadLibrary had succeeded) plus a 4663 Execute event;
  - the installed app with `CORECLR_*` logged a 4663 Execute event.
- An earlier event-log-only probe reported PASS for the helper. **That was a false negative**: CoreCLR does not report profiler failures to the event log, and it unloads a failed profiler before the module list can be read. It is superseded.

**F4 attack precondition: CONFIRMED over RDP (check A3).** A harmless `DOTNET_PM_ENV_PROBE`, set without elevation in the admin user's `HKCU\Environment`, reached the consent-elevated helper. The guard refused with exit 69. The same path delivers `CORECLR_*` profiler variables, so F4 is exploitable by any medium-integrity process of an admin user at the next UAC approval.

*Earlier wording, kept for the record:* whether the UAC-elevated helper actually gets these variables from `HKCU\Environment` had not been verified through real UAC. In the VM test, SYSTEM set them directly. With over-the-shoulder elevation (standard user plus administrator credentials), the elevated process normally gets the administrator's profile environment. The exposed case is code running at medium integrity as an administrator in Admin Approval Mode. See the RDP checks.

**Required fix (not done):** start the elevated helper with an environment its caller cannot influence. Options:
- a service-hosted helper, started on demand with the SCM's clean environment. The pipe ACL and client-authentication model must be redesigned for this.
- a native or NativeAOT entry point that clears the environment before any runtime starts.

This host has no native toolchain (no MSVC, clang or link.exe), and either option is an architectural change that needs review.

## Standard-user and IPC results (final installed 0.5.1 build, run `final051`: **101 PASS, 0 FAIL**; earlier rebuild `fixed051r2`: 100 PASS, 1 FAIL)

- **81/81 install-folder checks denied for the standard user:**
  - write, delete, WRITE_DAC and take-ownership on every installed file;
  - planting `version.dll`, `DbgHelp.dll`, `PartitionManager.Helper.dll` and a probe file;
  - creating subfolders;
  - directory WRITE_DAC, WRITE_OWNER and DELETE.
- **Policy:** creating `HKLM\SOFTWARE\PartitionManager` is denied. The policy key is absent, so writes are disabled.
- **IPC rejections and refusals:**

  | Case | Result |
  |---|---|
  | Standard user connects to the helper pipe | UnauthorizedAccess; the helper keeps waiting with no session |
  | Wrong token | `BAD_TOKEN` |
  | Correct token from a forged PID | `WRONG_PEER` |
  | Correct token and PID from a non-PartitionManager image | `WRONG_IMAGE` |
  | 2 GiB length prefix | `BAD_LENGTH` |
  | Unknown JSON member | `MALFORMED` |
  | Pre-created pipe name | exit 67 |
  | Launched with a standard-user token | Windows error 740, elevation required |

- **Audit data:**
  - creating folders in the data root is denied;
  - creating files in the audit folder is denied;
  - write and delete on `journal.lock` are denied.
- **The one FAIL in `fixed051r2` was a test artifact, not a product defect:**
  - It was `C:\Windows\SystemTemp\.net\PartitionManager\…` holding the 5 WPF `*_cor3.dll` files.
  - My F4 positive control created it by running the self-extracting app as SYSTEM at 15:38:30 UTC.
  - SystemTemp is admin-only. I verified the folder's contents and removed it (`cleanup-systemtemp-extract-result.json`).
  - The app's normal per-user extraction into the user's own `%TEMP%` gives no privilege change and is accepted for the preview.

**What SYSTEM automation does not prove:**
- Legitimate app-to-helper communication through a real interactive UAC prompt.
- UAC rejection handling in the UI.
- Whether `HKCU\Environment` reaches the elevated helper.

These are in [RDP-ManualChecks-0.5.1.md](../testing/RDP-ManualChecks-0.5.1.md) and remain **NOT TESTED**.

## Evidence

Local, in `artifacts/cloud-test-control/`:

| File | What it holds |
|---|---|
| `security-baseline-4-result.json`, `show-baseline4-result.json` | 0.5.0: 99 PASS / 1 FAIL (F1) |
| `precheck-squat-result.json`, `security-squat-existing-result.json`, `cleanup-squat-1-result.json` | F2 discovery and cleanup |
| `upgrade-0.5.1-result.json`, `upgrade-0.5.1-r2-result.json` | Upgrade and reinstall |
| `security-fixed051-result.json`, `show-fixed051-result.json` | First 0.5.1: 101/0 |
| `security-squatrepair051-result.json`, `show-squatrepair051-result.json` | F2 repair |
| `profiler-control-netfx2-result.json`, `profiler-control-app2-result.json`, `profiler-helper051-result.json`, `profiler-helper051r2-result.json` | F4 controls and helper runs |
| `profiler-baseline051-*`, `profiler-control-*` (without the 2 suffix) | Superseded event-log-only detector (false negative) |
| `security-fixed051r2-result.json`, `show-fixed051r2-result.json` | Rebuilt 0.5.1 |
| `upgrade-0.5.1-r3-result.json`, `security-final051-result.json`, `show-final051-result.json`, `profiler-helperfinal051-result.json` | Final build (policy key present for the RDP fixture, so the standard user's write to the existing policy key was also denied) |
| `manual-fixture-setup-result.json` | RDP fixture (disposable VHDX + policy) |
| `staging-bucket-0.5.1*.txt`, `sg-rdp-revocation.json` | Bucket lifecycle and ingress revocation |

On the VM:
- `C:\PMCloudTest-20261107\Security\*.json` (full rows);
- `C:\PMCloudTest-20261107\Upgrade*\` (installers and Inno logs);
- `C:\PMCloudTest-20261107\Security\repair-squatrepair051.log`.

Build: 377/377 tests (`artifacts/reports/test-results.trx`).

**Final 0.5.1-preview SHA-256 (unsigned; installed on the VM):**

| File | SHA-256 |
|---|---|
| `PartitionManager.exe` | `808e2bbd9c4a92acb8a0d2b5deb4043092379b4a460cd36311f4566325945fe4` |
| `PartitionManager.Helper.exe` | `194277da1b69aecfb2fddcc73642a566433376e802d63343510da6dc0f319497` |
| `PartitionManager-0.5.1-preview-x64-setup.exe` | `17040966eccaa4bb358b8611837895015e7d9ce810a0d72a580e2235dd46a207` |

Superseded intermediate builds (installed and tested, then replaced):
- installer `c163c0c5…`, helper `ced891b5…`: before the environment guard;
- installer `4fb51f5a…`, helper `a415e7a4…`: before the client early-exit messages.

The final build adds:
- `HelperExitCodes` and `HelperClient.ConnectWhileRunningAsync`: the app reports the helper's refusal reason (for example exit 68 or 69) at once, instead of a generic error after 30 s;
- the pipe handle is now also disposed when the squat check fails.
