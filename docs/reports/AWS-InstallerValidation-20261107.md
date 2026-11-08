# AWS installer validation — 2026-11-07

Executed on the user-designated disposable EC2 Windows Server 2025 Datacenter VM in ap-south-1 via Systems Manager, as SYSTEM. This is real installed-package testing, not simulation. It does not establish Windows 10/11 compatibility or production readiness.

| Check | Result |
|---|---|
| EC2 identity | PASS: Amazon EC2 manufacturer and IMDSv2 instance ID matched the explicitly targeted VM |
| Installer transfer integrity | PASS: SHA-256 `83B31BF0C88BCB127D589344051DC1B7325C82A3B3451A13F8EF7E45DC5122D2` |
| Fresh installation | PASS: exit 0; app/helper present in Program Files |
| Repair by rerunning installer | PASS: exit 0 |
| Uninstall | PASS: exit 0 and unrelated sentinel preserved; exhaustive removal/registry-residue audit not performed |
| Reinstall | PASS: exit 0 |
| Installed app and helper integrity | PASS: both SHA-256 hashes match local published executables |
| Helper invocation without arguments | PASS: rejected with expected exit code 64 |
| Installed executable ACL inspection | SYSTEM/Administrators full control; Builtin Users read/execute; actual standard-user replacement attack remains pending |
| Partition layout | Unchanged across lifecycle, comparing disk/partition numbers, GUIDs, offsets and sizes |

Evidence: `artifacts/cloud-test-control/preflight-result.json`, `lifecycle-result.json`, `installed-check-result.json`, and `installed-evidence.json`. Remote logs remain at `C:\PMCloudTest-20261107\Evidence`. Lifecycle command ID: `9a2bfaaa-d664-47e0-b0e1-97c84391ee63`; installed checks: `7aa18945-7ba4-4598-8433-b55b49dbffa2`.

No disk initialization, repartitioning, physical E: writes, or crash/reboot was performed. SYSTEM execution does not test interactive UAC approval/rejection, a standard-user session, authenticated app-to-helper IPC, GUI accessibility, DPI, or recovery from interruption. Upgrade from an older release and Windows 10/11 testing remain pending. No VM snapshot/restore validation was performed.

The test installer and reinstalled app remain on the VM for subsequent tests. The dedicated `PartitionManagerTestSSM` role/profile remains attached so remote testing can continue. The EC2 instance remains running; existing AWS compute/storage charges continue according to the account's terms. The temporary private S3 transfer object and bucket were deleted after hash verification. No permanent public download was created.

The harness now accepts an explicit expected EC2 instance ID, verifies it through [AWS IMDSv2](https://docs.aws.amazon.com/AWSEC2/latest/UserGuide/configuring-instance-metadata-service.html), and still requires disposable-VM confirmation. The updated PowerShell script passed syntax validation and executed successfully in the identified EC2 VM. Application binaries were not rebuilt or changed by this test.
