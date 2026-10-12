# Contributing

Use x64 Windows and the SDK pinned in global.json. The application and worker must stay unprivileged; only Woi.Capture may require administrator elevation. Development-only Microsoft TraceProcessing binaries are not part of the product package.

Run scripts/test.ps1 -DownloadFixtures before submitting changes. This verifies dependency locks, builds Release, and tests real Microsoft ETLs. Run scripts/publish.ps1, then scripts/stress.cjs against the packaged worker. Documentation describes remaining platform and validation limits; do not replace unperformed tests with claims.

Changes to recorded measurements, lifetime identity, capture profiles, or cache formats require corresponding evidence and focused regression coverage. Increment the analysis version when derived interpretation changes. Keep original ETLs read-only, unavailable measurements nullable, and capture cancellation scoped to the owned instance.

Do not commit or attach private ETLs, saved .woi investigations, interrupted-save files, memory dumps, or evidence databases to public issues or pull requests. Git and source-export exclusions cover these file types; they do not sanitize Markdown/JSON reports or remove previously committed files. Reports may contain paths, command lines, addresses, endpoints, machine metadata, and notes. Review filenames and contents, redact a minimal reproduction, or use the public fixtures. Run scripts/test-source-export.ps1 when changing packaging exclusions.

Guided capture requires Windows administrator elevation for the same account as the UI. Test start, manual stop, automatic stop, cancellation, UI exit, helper recovery, and coexistence with another named WPR recording. Provider requests are not proof of captured coverage.
