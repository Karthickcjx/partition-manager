# RDP manual checks — 0.5.1-preview on the disposable EC2 VM

Automation runs through SSM as SYSTEM, so it can't show a real UAC prompt, approve or decline it, or exercise the UI. These checks need a person at an interactive desktop, about 30–40 minutes. Record PASS / FAIL / NOT TESTED for each row in the table at the end and send it back. A screenshot of each dialog helps.

**Fixture already prepared on the VM**
- Helper policy: `WriteMode=VirtualDisksOnly`, `AllowedImageRoots=C:\PMCloudTest-20261107\Disks`.
- One disposable 512 MiB VHDX at `C:\PMCloudTest-20261107\Disks\pm-rdp-test.vhdx`, attached as disk 1, with NTFS volume **D: "PMRDPTEST"** holding 5 hashed sentinel files.
- **Only operate on D: PMRDPTEST.** Do not touch disk 0, which is the VM's boot disk.
- If the VM restarts, the VHDX detaches. Ask me to re-attach it with `vm-manual-test-setup.ps1 -Reattach`.

## 1. Connect without opening any public port

The VM's security group has **no inbound rules**. RDP goes through an SSM port-forwarding tunnel.

1. On your PC, install the AWS **Session Manager plugin** (it is not installed now). Get it from the official AWS documentation page "Install the Session Manager plugin for the AWS CLI" (Windows `SessionManagerPluginSetup.exe`).
2. Open a terminal and keep it open:
   ```powershell
   aws ssm start-session --region ap-south-1 --target i-012dadc6397d5add9 `
     --document-name AWS-StartPortForwardingSession --parameters "portNumber=3389,localPortNumber=53389"
   ```
3. Create the two test accounts **yourself**, so that no password passes through me or any log. In a second terminal:
   ```powershell
   aws ssm start-session --region ap-south-1 --target i-012dadc6397d5add9
   # inside the session (PowerShell on the VM):
   net user pmrdp-std * /add            # prompts for a password; type it twice (not echoed)
   net localgroup "Remote Desktop Users" pmrdp-std /add
   net user pmrdp-adm * /add
   net localgroup Administrators pmrdp-adm /add
   exit
   ```
   - `pmrdp-adm` is a normal admin with Admin Approval Mode, so it gets real UAC prompts.
   - The built-in `Administrator` account does not get UAC prompts on Server by default, so **don't use it for these checks**.
4. Run `mstsc /v:localhost:53389` and sign in as `.\pmrdp-std`.

## 2. Checks as the standard user (`pmrdp-std`)

| # | Steps | Expected |
|---|---|---|
| S1 | Start Partition Manager from the Start menu. | Opens **without** a UAC prompt. In Task Manager › Details (add the "Elevated" column), PartitionManager.exe shows Elevated = No. Disks, including the 512 MiB virtual disk and D: PMRDPTEST, are listed read-only. |
| S2 | Use only the keyboard: Tab, Shift+Tab, arrow keys, Enter, Esc, Alt shortcuts. Go to the disk map, select D: PMRDPTEST, and open the Change Label dialog. | Every control is reachable, the focus outline is always visible, Esc closes dialogs, and nothing traps focus. |
| S3 | Queue **Change label** on D: to `PMRDP2`, then choose Apply. In the confirmation dialog, choose **Cancel**. | No UAC prompt. Nothing changes; the operation stays queued or is discarded as the dialog says. The label is still `PMRDPTEST`. |
| S4 | Apply again and confirm. When UAC asks for administrator credentials, choose **No** or close the prompt. | A visible message: "Administrator permission was declined, so nothing was changed." The label is unchanged. The app keeps working. |
| S5 | Apply again, confirm, and in UAC enter the `pmrdp-adm` credentials (over-the-shoulder elevation). | The helper starts and the label changes to `PMRDP2`. The app's progress and result are shown and the disk view refreshes. |
| S6 | Try a task the helper must refuse. Select disk 0 / C: and try any write, or delete on D:. | Physical disk: the task is simulated only or refused with a clear reason, and there is no write. Delete on the virtual disk follows its own confirmation. **Skip delete if unsure.** |
| S7 | In File Explorer, try to copy any file into `C:\Program Files\PartitionManager` or `C:\ProgramData\PartitionManager\Audit`. | Denied, or asks for admin credentials (cancel it). |

## 3. Checks as the administrator (`pmrdp-adm`, Admin Approval Mode)

Sign out, then sign in as `.\pmrdp-adm`.

| # | Steps | Expected |
|---|---|---|
| A1 | Start Partition Manager. Change the D: label back to `PMRDPTEST`: Apply, confirm, then **Yes** at the consent prompt. | Consent prompt (no password). The operation succeeds. |
| A2 | Repeat with a new label and choose **No** at consent. | Same declined message as S4, nothing changed. |
| A3 | **Environment propagation (this decides how exposed finding F4 is).** Close the app. Run `[Environment]::SetEnvironmentVariable('DOTNET_PM_ENV_PROBE','1','User')` in a normal (non-elevated) PowerShell, then sign out and sign in again. Start the app and apply a label change, choosing **Yes** at consent. | If UAC passes HKCU\Environment to the elevated helper, the app shows **"The privileged helper refused to run because .NET runtime or profiler variables … are set in your user environment …"** within a few seconds and nothing changes. If the label change succeeds instead, the variable did **not** reach the helper; record that. `DOTNET_PM_ENV_PROBE` is not a runtime setting and is harmless. |
| A4 | Remove the variable with `[Environment]::SetEnvironmentVariable('DOTNET_PM_ENV_PROBE',$null,'User')`, sign out and in again, and retry the label change. | Succeeds. |
| A5 | Repeat A3 as `pmrdp-std` (over-the-shoulder, entering the `pmrdp-adm` password). Set the variable in **pmrdp-std**'s own environment. | Expected: success. The elevated process uses the administrator's profile environment. Record what happens. |

## 3b. Real Move checks (as `pmrdp-adm`, on D: PMRDPTEST only)

Real Move works on the approved virtual disk only; Merge and anything on Disk 0 stay simulated. Tell me before M1 so I can
record the sentinel hashes, and after each step so I can verify them from the VM.

| # | Steps | Expected |
|---|---|---|
| M1 | Close any Explorer window showing D:. In the app select **D: PMRDPTEST** → **Resize/Move**, keep the size, increase the space **before** the partition by 64 MB (move right). Apply → confirm → UAC Yes. | Header chip says "Real writes: virtual disks"; the confirmation does not say "simulated". Completes and verified; D: keeps its letter and label; files still open fine. |
| M2 | Move it back left by 32 MB the same way. | Same as M1. |
| M3 | Open D: in Explorer and leave a file open (e.g. a .txt in Notepad), then try a Move. | "The volume is in use … Nothing was changed." with title **Nothing was changed**. Close the file, retry: it succeeds (a refusal no longer blocks later operations). |
| M4 | Queue a Move **and** a size change in the same dialog. | Shown as simulated, with a reason, before anything runs. |
| M5 | Try Merge, or a Move on Disk 0. | Simulated only (by design). |

## 3c. C: shrink/extend (this VM only, owner-approved) and Data Recovery

C: (Disk 0) is enrolled for **in-place shrink and extend only** (Windows' own resize). Move, delete, format, label or letter
changes on C: stay refused. Recovery snapshot: `snap-01dadc8708ef7db03` (taken just before enrolment).

| # | Steps | Expected |
|---|---|---|
| C1 | Partition Management → select **Windows (C:)** → Resize/Move → shrink by 5 GB (do not move it) → Apply → confirm → UAC Yes. | Confirmation shows a real operation (not "Simulation"); completes and verified; C: is 5 GB smaller and there is unallocated space after it. |
| C2 | Extend C: back into that space. | Completes and verified; C: back to its original size. |
| C3 | Try delete/format/move of C:, or anything on EFI/MSR. | Refused or simulated with a reason. |
| R1 | Data Recovery page: the app is **not** elevated (no "Restart as administrator" button). Pick D: PMRDPTEST → Scan → approve UAC. | Scan runs through the helper; results listed. |
| R2 | Tick a few found files → Recover → choose a folder on C: (a different drive). | Files saved there, owned by you; nothing written to D:. |

## 4. Interruption checks (the recovery point exists: `snap-0cb61fae2de784052`)

Do these last, as `pmrdp-adm`, only after sections 2–3. **Message me before each one.** I arm a SYSTEM watcher that interrupts the operation the moment its journal records `StepStarted`, so you don't need to time anything. Use only the disposable virtual disk (disk 1).

| # | Steps | Expected |
|---|---|---|
| R1 | I arm `-Mode Kill`. In the app, queue **Create partition** in the free space of the 512 MiB virtual disk (NTFS, any label), apply and approve. | The helper is killed mid-operation. The app shows a visible error such as "helper closed the connection unexpectedly …", does not claim success, and asks you to refresh. |
| R2 | Refresh, then try another label change on D:. | Refused with **RECOVERY_REVIEW_REQUIRED** (interrupted journal), and nothing changes. Note the exact message. |
| R3 | Tell me. I record the layout, sentinel hashes, journal and disk 0 and compare them with the baseline. After review, I move the interrupted journal into the evidence folder (kept, not deleted) so the next test can run. | — |
| R4 | I arm `-Mode Restart`. Repeat R1. | Windows restarts mid-operation and RDP disconnects. Wait about 3 minutes and reconnect through the same tunnel (restart the port-forward command if it closed). |
| R5 | After the restart, open the app and try a label change. | **RECOVERY_REVIEW_REQUIRED.** I re-attach the fixture and compare the layout, hashes and journals. |

These are a process kill and a forced OS restart. **They do not prove recovery from power loss or device failure.**

## 5. After the checks

Tell me the results. I will then:
- verify the label, journal and audit entries and the sentinel hashes (`vm-manual-test-setup.ps1 -Verify`);
- remove the fixture and policy key (`-Teardown`).

**You** should then delete the two accounts in an SSM session (`net user pmrdp-std /delete`, `net user pmrdp-adm /delete`) and close the port-forwarding terminal.

| # | Result | Notes / screenshot |
|---|---|---|
| S1 | | |
| S2 | | |
| S3 | | |
| S4 | | |
| S5 | | |
| S6 | | |
| S7 | | |
| A1 | | |
| A2 | | |
| A3 | | |
| A4 | | |
| A5 | | |
| R1 | | |
| R2 | | |
| R4 | | |
| R5 | | |
