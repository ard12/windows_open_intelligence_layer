# Product roadmap and gates

The [detailed engineering timeline](engineering-plan.md) sets dates, deliverables, dependencies, and acceptance gates for the next release and conditional live recorder.

One application translates recorded Windows activity into incident investigations with progressively deeper evidence. Accessible descriptions, resource rankings, and explicit evidence quality are the product hypothesis; adoption remains unproven.

| Stage | Status | Acceptance gate |
|---|---|---|
| 1. Offline analyzer | Implemented and hardened; three reference fixtures meet CPU-weight gate | Broader WPA/schema checks, rich-resource maximum-envelope decoding, Windows 11 and independent usefulness |
| 2. Guided bounded WPR capture | Implemented preview; both profiles and lifecycle pass Windows Server CI | Windows 10 save-timeout resolution, Windows 11 lifecycle/coverage, privileged helper trust/storage |
| 3. Live recorder | Manual bounded in-memory observation, saved investigations and comparison implemented; persistent/background recorder planned | Repeated filled-history acceptance, observer cost and eventual 24-hour overhead/retention benchmark |
| 4. Deeper investigation | Planned, including execution/dependency visualization | Additional collection and validated relationship semantics; measured file/Registry, stack, scheduling and wait costs |
| 5. Service/security and Sysmon | Planned | Source identities and provenance survive correlation |
| 6. Optional natural language | Planned | Cited evidence, preserved uncertainty, explicit cloud opt-in |

Guided capture was implemented at the user's request while remaining analyzer correctness and product gates stay visible. Implementation does not mark these gates passed.

The eventual persistent recorder should preserve one-second Windows API snapshots alongside ETL, because snapshots are separate evidence. Keep the UI unprivileged and a collector narrowly scoped. Memory buffering and real-time delivery need separate arrangements. Circular storage bounds capacity, not cumulative writes.

Future experimental recorder targets: below 1% idle machine CPU, below 2% during representative workloads, below 512 MiB headless memory, and below 100 MiB/hour background writes. Measure retained history, workload slowdown, and event loss. None is a current performance claim.

Drivers, eBPF, temperatures, exhaustive heaps, and automatic remediation remain outside the roadmap. TraceEvent is the parsing foundation; System Informer and OpenProcMon are references with no copied code or driver included. Actual reused components retain their notices.
