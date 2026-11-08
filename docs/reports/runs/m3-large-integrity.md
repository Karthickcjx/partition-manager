# Large sparse VHDX

PASS: 16 GiB dynamic VHDX, 8 GiB NTFS shrunk to 4294967296 bytes through helper; independent Get-Partition check.
1003 files re-read and SHA-256 matched, including a 128 MiB file, Unicode, read-only and >260-character deep path.
1 MiB write buffer; streaming hash reads. Image file length before cleanup: 306184192.
Not tested: near-full/fragmented media, device I/O fault, power loss.
