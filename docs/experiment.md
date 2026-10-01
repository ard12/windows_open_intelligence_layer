# Investigation experiment protocol

1 October 2026. This protocol prepares P3 of the product reassessment. No participant results or independent accuracy review have been collected. The earlier offline pilot in pilot.md remains a useful task subset; this protocol adds the symptom-to-next-measurement and comparison workflow.

## Recruitment and release prerequisites

Recruit 8–10 power users and hands-on IT people with recent Windows slowdown investigations; complete at least eight sessions in at least three independent contexts. Include whole-PC and application-specific symptoms. Use the self-contained package on at least two named Windows 11 x64 machines, including an 8–16 GiB machine. Record package hash, OS/build, machine specifications, prerequisite/setup time, and developer assistance. This checkout's Windows 10 checks do not substitute for clean-machine use.

Use a labelled engineering preview only inside the tested envelope. Do not ask a participant to use a capture profile whose native capture-to-evidence gate remains unverified. Snapshot observation, supplied ETL analysis, and saved investigations can be evaluated independently of native capture. Record that limitation explicitly.

## Task design

Start timing at app launch, including any setup, observation, capture, or import. Use distinct equivalent tasks and counterbalance normal-workflow-first versus app-first order. Compare participants' normal tools; for history, include System Informer when that is their normal workflow. Do not require familiarity with WPA.

Each session includes:

1. A seeded supported slowdown with a known interval and controlled contributor. Ask the participant to find what changed, locate one supporting snapshot or ETL event, and state the limitation in ordinary language.
2. A real recent investigation or a reproducible real task. Ask for a useful next measurement or decision and have an independent reviewer assess whether the evidence supports it.
3. A benign, missing-data, or unsupported-cause control. Record whether the participant mistakes resource attribution for a cause or absent measurements for zero.
4. Two user-confirmed attempts at equivalent work after a stated change. Include a task where lower CPU and shorter duration disagree. Ask what can and cannot be concluded.
5. Save/reopen and report inspection without developer help. Ask what private information is included and verify that original ETL files were not exported by default.

Let participants work independently; log every hint with its time and effect. Interview afterwards. Do not count a correct answer produced after a substantive developer hint as independent completion. Collect voluntary reuse only when an investigation opportunity occurs; no mandatory short-window retention count applies.

## Independent accuracy set

Before evaluation, an independent reviewer supplies or records twelve held-out scenarios and locks expected observations, unsupported conclusions, acceptable abstentions, and useful next steps. Store fixture hashes and source/coverage metadata in the results ledger before scoring. Keep these captures separate from tuning examples and automated unit fixtures. Changes after expected outcomes are revealed require a new held-out set or a declared retest.

Cover sustained CPU with an affected app, busy innocent processes, application delay with low CPU, high private allocation without evidence of shortage, accounted I/O without disk coverage, PID reuse/exit, missing providers, missed ticks/sleep, clock changes, benign idle marks, mismatched comparison work, and different application versions. Record whether each case actually has enough supported inputs to expect a non-abstaining answer. Do not equate twelve automated unit tests with twelve independent held-out scenarios.

## Evidence ledger and thresholds

Use experiment-results.json. Empty arrays mean no results exist. Each session entry needs an anonymous participant ID, context ID, package/machine identifiers, task order, normal/app elapsed seconds, independent interval interpretation, accepted next step, evidence located, coverage understanding, all hints, incorrect causal statements, privacy/setup problems, and the reviewer's rationale. Keep consent/contact details and raw user traces outside the repository.

The decision gates from product-reassessment.md are preserved:

- At least 6/8 independently identify a useful interval and correctly explain what the finding establishes and what it does not.
- At least 5/8 reach an independently accepted useful next step without developer help. Document three real investigations where output changes the next measurement or decision.
- At least 10/12 held-out scenarios have correct expected observations/limitations; zero confidently unsupported causal statements; useful non-abstaining output on at least 75% of supported cases.
- Record normal-workflow task times. A 30% improvement is a useful target, not a mandatory market gate; enabling an otherwise inaccessible correct investigation also counts.
- Complete independent package use on two Windows 11 machines and record support/maintenance effort. Meet provisional observation budgets and never silently lose history or join the wrong lifetime.

At review, choose proceed, repair, or reduce scope from measured usefulness, correctness, and maintainability. Record actual denominators and missing results; do not turn an incomplete study into a pass. The default current decision is pending evidence.
