> Historical evidence through v0.3. For the freshly measured v0.4 results and current blockers, see [Milestone3.md](Milestone3.md).

# Data integrity: 0.3.0-alpha

Real structural operations ran through the privileged helper's `WriteEngine` and the real `WmiStorageWriteBackend`
on disposable VHDX images in `D:\PartitionManager-TestDisks` (run **20261107-141235**; full table in
[runs/StructuralRun-20261107-141235.md](runs/StructuralRun-20261107-141235.md)).

## Method

1. Create a 600 MB NTFS partition (through the helper), mount it with a drive letter, and write three files with
   random content through normal file APIs, using write-through and a flush: `a.bin` (1 MiB), `b.bin` (5 MiB) and
   `dir\c.bin` (20 MiB). Record their SHA-256.
2. After **every** later structural step, re-read all three files from the volume and compare the hashes.
3. Verify every layout change independently with `Get-Partition` / `Get-Volume` (Storage PowerShell module, not the
   app's own WMI code).

## Results

| Checkpoint | GPT disk | MBR disk |
|---|---|---|
| After create (baseline) | 3/3 match | 3/3 match |
| After shrink 600 → 400 MB | 3/3 match | 3/3 match |
| After extend 400 → 500 MB | 3/3 match | 3/3 match |
| After split 500 → 300 MB + 200 MB new partition | 3/3 match | 3/3 match |
| After formatting the split-off partition (exFAT) | 3/3 match | 3/3 match |
| After deleting the split-off partition | 3/3 match | 3/3 match |
| After a Split whose create step was forced to fail (the real shrink to 259 MB had already happened) | 3/3 match | – |
| After the documented recovery (extend back to 300 MB) | 3/3 match | – |

**14 of 14 checkpoints: all SHA-256 hashes matched.**

## Limits of this evidence

* Small volumes (≤ 600 MB) holding 26 MiB of data, on virtual disks backed by an NVMe SSD. It doesn't cover large
  or nearly full volumes, fragmented files, files in use during the operation, or slow or failing media.
* The partial-failure case was injected *between* Windows calls. Interruptions *inside* a Windows call (power loss
  during `Resize`) weren't tested and can't be made safe by this app.
* No physical disk was written.
