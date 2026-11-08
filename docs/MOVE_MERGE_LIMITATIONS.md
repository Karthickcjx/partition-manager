# Move and Merge: unsupported for real execution in 0.4.0-alpha

Windows Storage exposes create, delete, resize and access-path methods, but no
universal partition relocation or volume merge operation. See Microsoft's
[MSFT_Partition reference](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/msft-partition).
[FSCTL_MOVE_FILE](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_move_file)
moves file clusters **within one volume**, not a partition to a new extent.
Changing only a partition's start offset is never an acceptable implementation.

The inherited staged prototype creates/formats a destination, copies files,
hashes them, then deletes the source; Merge copies into a unique folder and may
extend its target. Inspection found these release blockers:

- Manifest enumeration precedes the attempt to freeze the source. New files can
  appear between enumeration and freezing and be omitted at cutover.
- Setting a partition read-only and probing file creation does not prove that
  existing handles, filesystem caches or concurrent actors are quiescent.
- ACLs, ownership, object IDs, quota metadata and some filesystem metadata are
  not preserved. The scanner omits recycle/system folders and treats certain
  metadata-query failures as absence of special content.
- No durable transaction journal, verified recovery backup or restart recovery
  exists. A helper crash can leave temporary volumes or a read-only source.
- Merge does not independently protect/verify every pre-existing target file.
  Retrying deletion after an uncertain failure is unsafe without re-enumeration.
- Memory for manifests and hash dictionaries grows with file count; only the
  transfer buffer is bounded. No general bounded-memory scalability claim.

The shipped helper returns `UNSUPPORTED_OPERATION` for both kinds. UI routing
keeps them **Simulated**. The existing implementation remains in source for
development tests, behind an internal friend-test seam that is never configured
by Program, IPC, registry, environment or command-line arguments. Tests using
that seam are prototype evidence, not validated product functionality.

The baseline nonadjacent-Merge failure was fixed: a gap no longer counts as
adjacency. This does not remove the blockers above. No actual Move/Merge was
run on user data or VHDX in this milestone.

Future work requires a consistent snapshot or exclusive-volume strategy,
metadata-complete copying or validated block migration, durable recovery records,
destination verification before cutover, reconciliation after uncertain calls,
and interruption tests in disposable VMs. Neither operation is atomic or
proven safely reversible.

Initialization uses the documented
[MSFT_Disk.Initialize](https://learn.microsoft.com/en-us/windows-hardware/drivers/storage/initialize-msft-disk)
method (MBR=1, GPT=2), with fresh blank-image approval and re-enumeration.
The native image check uses
[GetVirtualDiskPhysicalPath](https://learn.microsoft.com/en-us/windows/win32/api/virtdisk/nf-virtdisk-getvirtualdiskphysicalpath)
to correlate the approved file with its attached device, independently of WMI's
reported location. A version-2 metadata-only, read-only handle avoids requesting a conflicting
writable image handle during an existing attachment.
