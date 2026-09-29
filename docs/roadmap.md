# Product roadmap and gates

The [detailed engineering timeline](engineering-plan.md) sets dates, deliverables, dependencies, and acceptance gates for the next release and conditional live recorder.

One application translates recorded Windows activity into incident investigations with progressively deeper evidence. Accessible descriptions, resource rankings, and explicit evidence quality are the product hypothesis; adoption remains unproven.

| Stage | Status | Acceptance gate |
|---|---|---|
| 1. Offline analyzer | Implemented and hardened | Native CPU-weight agreement, WPA checks, maximum-envelope decoding, Windows 11 and pilot |
| 2. Guided bounded WPR capture | Implemented in v0.3.0 preview | Successful native lifecycle, workload coverage, interrupted recovery and coexistence |
| 3. Live recorder | Planned | 24-hour overhead and retention benchmark |
| 4. Deeper investigation | Planned | Measured costs for file/Registry, stacks, scheduling and waits |
| 5. Service/security and Sysmon | Planned | Source identities and provenance survive correlation |
| 6. Optional natural language | Planned | Cited evidence, preserved uncertainty, explicit cloud opt-in |

Guided capture was implemented at the user's request while remaining analyzer correctness and product gates stay visible. Implementation does not mark these gates passed.

The live recorder should preserve one-second Windows API snapshots alongside ETL, because snapshots are separate evidence. Keep the UI unprivileged and a collector narrowly scoped. Memory buffering and real-time delivery need separate arrangements. Circular storage bounds capacity, not cumulative writes.

Future experimental recorder targets: below 1% idle machine CPU, below 2% during representative workloads, below 512 MiB headless memory, and below 100 MiB/hour background writes. Measure retained history, workload slowdown, and event loss. None is a current performance claim.

Drivers, eBPF, temperatures, exhaustive heaps, and automatic remediation remain outside the roadmap. TraceEvent is the parsing foundation; System Informer and OpenProcMon are references with no copied code or driver included. Actual reused components retain their notices.
