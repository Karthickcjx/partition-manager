# Security review: 0.4.0-alpha

Internal engineering review only; no independent security certification.
The user-space attacker model includes malicious/stale IPC messages, image path
aliases, replay, replaceable privileged files and concurrent layout changes.

## Changes and evidence

- **Move/Merge data loss exposure contained:** shipped helper rejects real
  migration; prototype tests cannot enable it over IPC or configuration.
- **Physical disk gate retained:** fresh discovery, stable identity and virtual
  disk classification are checked independently of the frontend. No physical
  exception exists, including for E:. The E-scope module is never consulted here.
- **Native image correlation:** WMI's backing path is now cross-checked through
  OpenVirtualDisk/GetVirtualDiskPhysicalPath. Reparse ancestors are rejected;
  a read handle without delete sharing pins the backing file during the request.
  Metadata-only version-2 opens work for attached, formatted images. This does
  not make subsequent WMI partition changes atomic against another administrator.
- **Path aliases:** alternate streams, slash aliases, dot/traversal segments and
  trailing-dot/space components rejected in helper image policy.
- **Initialization:** requires RAW with no partitions and exactly one fresh
  administrator-policy approval bound to full identity/layout/image path.
  Missing, duplicate, stale, future and replacement-disk approvals are refused.
  Approval is not accepted from IPC. The integration factory is the trusted
  issuer immediately after creating a new parentless, empty image. Product UI
  issuance/provisioning is not implemented; manual approval of arbitrary RAW
  images is not supported or validated as safe.
- **Post-write substitution:** disk replacement at the same number is reported
  unverified; ordinary operations compare stable identity, size and image path.
  Initialization allows the disk UniqueId change caused by creating its table.
- **Uncertain failures:** a failed shrink no longer asserts unchanged state.
  Client cleanup no longer kills the helper after five seconds of disconnected
  IPC; a Windows storage call is allowed to finish and be verified.
- **Privileged location:** removed development override; inspect owner and write
  ACLs of folder/files, reject reparse paths and unsafe ancestor permissions.
  Elevated logging no longer writes to user-controlled LOCALAPPDATA. Audit setup
  refuses untrusted pre-created locations instead of taking ownership of them.
- **Installer:** removed recursive uninstall directory deletion, which could
  remove unrelated user data placed beside the application.

Existing authenticated IPC tests remain passing: token length/constant-time
comparison, actual client PID/image, server PID, malformed frame/unknown fields,
size limits, stale timestamps and repeated request IDs. Real pipe ACL and UAC
tests from an installed app still require the isolated VM matrix.

## Open release gates

1. No code signing or independent security review. Artifacts are unsigned and
   must not be presented as production-ready.
2. Installed ACLs, single-file native extraction/DLL search behavior, audit
   location deployment and standard-user replacement attacks are not validated
   end-to-end in a VM. Treat these as high-priority release blockers.
3. Image-root ownership and ACL policy is not fully enforced. Native correlation
   and file pinning reduce substitution risk but do not prove exclusive access
   to all backing data or all path ancestors. Only administrator-controlled
   disposable image storage is in scope.
4. WMI storage operations address disk/partition numbers after validation. An
   external privileged tool may race multi-step execution. No exclusive device
   transaction or durable recovery journal exists. Verification detects some,
   not all, races. Audit records are not tamper-evident to administrators.
5. Initialization approvals rely on a trusted issuer's assertion of newly
   created empty media. Persistent one-use approval consumption and a protected
   product provisioning workflow remain unfinished. Default installed behavior
   denies initialization without approval.
6. Protection of both raw GPT copies, metadata-complete copying, real device fault
   injection, process death inside Windows calls and controlled power loss are
   pending. The experimental copier is not a production migration implementation.

No known uncontained critical issue is declared fixed merely by passing unit
tests. Any critical finding from the pending reviews blocks release. This build
is a **blocked engineering prerelease**, suitable for review on disposable media.
