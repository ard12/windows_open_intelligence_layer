# Contributing

Use x64 Windows and the SDK pinned in global.json. The application and worker must stay unprivileged; only Woi.Capture may require administrator elevation. Development-only Microsoft TraceProcessing binaries are not part of the product package.

Run scripts/test.ps1 -DownloadFixtures before submitting changes. This verifies dependency locks, builds Release, and tests real Microsoft ETLs. Run scripts/publish.ps1, then scripts/stress.cjs against the packaged worker. Documentation describes remaining platform and validation limits; do not replace unperformed tests with claims.

Changes to recorded measurements, lifetime identity, capture profiles, or cache formats require corresponding evidence and focused regression coverage. Increment the analysis version when derived interpretation changes. Keep original ETLs read-only, unavailable measurements nullable, and capture cancellation scoped to the owned instance.

Do not attach private ETLs to public issues. Reports may contain paths, command lines, addresses, and endpoints. Redact a minimal reproduction or use the public fixtures.

Guided capture requires Windows administrator elevation for the same account as the UI. Test start, manual stop, automatic stop, cancellation, UI exit, helper recovery, and coexistence with another named WPR recording. Provider requests are not proof of captured coverage.
