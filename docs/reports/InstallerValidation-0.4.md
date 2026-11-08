# Installer validation: 0.4.0-alpha

Status: **Not tested** in an isolated VM. No installation, repair, upgrade,
uninstallation or reinstallation was performed on the user's main PC.
No accessible isolated VM was identified in this environment.

Source changes: version 0.4.0-alpha; per-machine Program Files installation and
administrator privilege requirement retained. Removed recursive `{app}` deletion
from UninstallDelete: the installer log now removes installed files only, leaving
unrelated files and audit evidence intact.

The helper now ignores the former development-location registry override, checks
directory/file ownership and write ACLs, rejects reparse installation ancestors,
and refuses unsafe audit paths instead of taking ownership of them.
These source checks do not substitute for installed standard-user verification.

Required VM matrix before deployment: fresh install; standard-user inability to
replace helper/dependencies; DLL loading from protected paths; UAC acceptance and
rejection; real pipe ACL/client PID/server PID authentication; repair; upgrade from
0.3; uninstall with a sentinel unrelated file in the install directory; reinstall.
Snapshot VM before each destructive test and retain installer logs.

Installer compilation/signature evidence is recorded in Milestone3.md and the
artifact checksum manifest. Compilation is not lifecycle validation.
