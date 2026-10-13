# Working-app screenshots

These unedited PNGs were produced by the packaged WPF application's smoke-test workflow on 13 October 2026, using the locally validated 0.4.0-preview.1 build from source later committed as `28a5220`. They are real application views, not mockups. The presentation-only README/screenshot commit does not change those application binaries.

All three views use the public PerfView fixture `SimpleAllocator-dotnet6.etl`, fetched by [get-fixtures.ps1](../../scripts/get-fixtures.ps1). Source SHA-256: `5876415dbc49e6b86bd96906e77a2237deced01a8841684644f3bd303a1cf812`. Recorded process names and addresses belong to that public fixture. No live Observe view, personal investigation, credentials or private recording is included. The visible local path identifies only this project's fixture folder.

| File | View | SHA-256 |
|---|---|---|
| summary.png | Resource summary, contributors and limitations | `109b9473893dcb5c49fadb20704e43299b414b1a02b0c708fe9f87d2a1d3971c` |
| explorer.png | Captured process lifetimes and paged activity | `a9fe3df7309d948ed6bd875929812cb8f949addd5002476246a73d69a5031e12` |
| evidence.png | Event coordinates, fields and captured stack | `4f733bab653049499e11ee2f5ac513023445137f52966cfd3ef0e5775e58e52e` |

Values reflect this short recording. Missing estimates, unresolved paths, unknown schemas and incomplete stacks remain visible. Screenshots demonstrate the implemented workflow, not native capture acceptance or a diagnosis of application causality.
