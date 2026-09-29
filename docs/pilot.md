# Offline analyzer pilot protocol

Run this after trace correctness checks. This document prepares the study; no pilot participant has been tested yet.

Recruit eight participants: two everyday Windows users, two developers, two power users, and two IT/investigation users. Use the same controlled recording, provider settings, and task wording for each. Keep process names opaque enough that the expected answer is not visible in the instructions.

## Tasks

1. Find the interval with the sustained CPU burst. Identify the dominant contributor and describe the measurement in ordinary language.
2. Open one supporting CPU event. Verify its time and process lifetime. Explain whether the recording proves why the application became unresponsive.
3. Find the logical file activity, distinguish requested file bytes from physical disk transfers, and identify an evidence limitation.
4. Locate the memory allocation phase if memory snapshots exist. If they do not, describe the missing coverage without interpreting it as zero.

Before the analyzer trial, time the same first two tasks using the participant's existing workflow. Counterbalance tool order across participants. Start the two-minute timer when the recording is ready to inspect; record import/reopen times separately.

## Scorecard

| Participant | Audience | Existing workflow time | Analyzer interval + contributor time | Correct interval | Correct contributor | Evidence located | Coverage understood | Notes |
|---|---|---:|---:|---|---|---|---|---|
| 1 | Everyday | | | | | | | |
| 2 | Everyday | | | | | | | |
| 3 | Developer | | | | | | | |
| 4 | Developer | | | | | | | |
| 5 | Power user | | | | | | | |
| 6 | Power user | | | | | | | |
| 7 | IT/investigation | | | | | | | |
| 8 | IT/investigation | | | | | | | |

Expansion requires at least six participants to identify the relevant interval and dominant contributor within two minutes and then locate supporting evidence. Record incorrect diagnoses, missed unavailable data, navigation problems, and comparative task times. Revise summaries and navigation before adding capture features if users struggle.
