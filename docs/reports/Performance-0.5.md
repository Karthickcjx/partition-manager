# Performance — 0.5.0-preview

Same host, Release self-contained published executable, three process launches each. UI Automation polls first window and populated disk map; working set sampled at that point. OS caches were not cleared: these are not controlled cold-boot benchmarks. No reference-product speed comparison.

| Sample | Window ms | Disk map ms | Working set MB |
|---|---:|---:|---:|
| Before 1 | 834 | 1337 | 295 |
| Before 2 | 734 | 1143 | 295 |
| Before 3 | 751 | 1140 | 296 |
| After 1 | 4985 | 5439 | 239 |
| After 2 | 770 | 1231 | 238 |
| After 3 | 793 | 1253 | 238 |

Evidence: `artifacts/reports/m4-performance-before.txt` and `m4-performance-after.txt`. The after run occurred while release packaging was still active; the first launch is retained rather than discarded. Median disk-map time increased from 1143 to 1253 ms. **No startup speedup established.** Mean sampled working set fell from 295.3 to 238.3 MB (about 19%). Removing the large content shadow is consistent with that improvement, but a three-run before/after comparison is not causal proof and does not measure peak memory.

Separate read-only test recorded five enumeration samples: 611.9, 76.1, 77.1, 73.7, 71.5 ms (mean 182.0 ms, warm-up included). One hundred in-process named-pipe protocol round trips averaged 0.113 ms, range 0.050–1.156 ms. Raw values are in `m4-enumeration-ipc-performance.json`. This is protocol-loopback latency, not authenticated installed-helper latency or elevation startup.

Actual helper startup, CPU/frame profiling, sustained large layouts and representative storage-write throughput remain unmeasured in this milestone. No thresholds are asserted from these small samples. Discovery remains read-only; none of these measurements writes to E:.
