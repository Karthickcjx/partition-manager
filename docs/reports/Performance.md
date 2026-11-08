# Performance: 0.2.0-alpha

Measured with `scripts/measure-performance.ps1` on 2026-11-07. Machine: Intel Core i5-11260H, 23.7 GB RAM,
NVMe SSD, Windows 11 Home. Build: Release self-contained single-file exe. The workload is a cold start until the
disk map shows the C: volume, five runs, with one disk and six partitions on this PC.

| Run | Process start → main window | → disk map populated | Working set |
|---|---|---|---|
| 1 | 795 ms | 1189 ms | 291 MB |
| 2 | 749 ms | 1162 ms | 285 MB |
| 3 | 764 ms | 1159 ms | 290 MB |
| 4 | 774 ms | 1173 ms | 290 MB |
| 5 | 763 ms | 1151 ms | 285 MB |

WMI discovery time, from the app's own log during those runs: 130–136 ms.

Unit-test wall time (209 tests): about 0.2–0.3 s.

## Observations

* Discovery runs on a background task, so the UI thread isn't blocked. The visible delay after the window opens
  (about 400 ms) is discovery plus the first layout.
* **Working set of about 290 MB is the main concern.** Likely contributors: the self-contained single-file
  extraction of the WPF runtime, a `DropShadowEffect` on the large content card (it forces a software-rendered
  layer), and rebuilding every view-model on each queue change. To investigate: dotnet-counters and
  PerfView, `ReadyToRun`, trimming WPF-unused assemblies, replacing the effect.
* Not measured yet: disks with many partitions (50+ on GPT), many disks (10+), and execution throughput. Clone
  and backup engines don't exist yet.

## Data recovery (elevated run 20261107-134103)

| Workload | Measured |
|---|---|
| Deleted-file scan of E: (55.75 GB, nearly empty, 256 MFT records) | 9 ms |
| Deleted-file scan of D: (270 GB, 321,280 MFT records) | 0.98 s (about 327,000 records/s) |
| Recover a 24 MiB file (1 run) from E: to D: | 45 ms |
| Lost-partition search, 512 MB VHDX | 22 ms |

The MFT is read sequentially in 4 MiB chunks and parsed in memory. Recovery streams through a 1 MiB buffer, so memory
use doesn't grow with file size.
