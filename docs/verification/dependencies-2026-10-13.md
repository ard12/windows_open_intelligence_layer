# Dependency verification — 2026-10-13

The current locked graph has **no matching known NuGet advisories** in the feeds retrieved on this date. This is a package-advisory result, not a claim that every embedded native component is free of vulnerabilities. No package versions were changed during this verification.

## Repeatable release gate

After creating a fresh self-contained bundle, run from the repository root:

```powershell
./scripts/verify-dependencies.ps1 -BundlePath dist/WindowsOpenIntelligence -OutputPath .artifacts/dependency-verification/report.json
./scripts/test-dependency-verification.ps1
```

PowerShell 7 and the pinned SDK are required. The verifier reads existing package archives and downloads only public advisory/release JSON, never dependencies. Honor its thrown error/nonzero exit; a previous report is not evidence for a later failed run. The ordinary application does not perform these network requests.

The verifier:

- Inventories every non-project entry in all source, test, and tool lock files, including transitive and non-Windows packages.
- Recomputes package content hashes using the SDK's official NuGet implementation and compares them with locked hashes. A signed NuGet package's content hash differs from its whole-archive SHA-512; both are recorded without conflating them.
- Reads each shipped executable's selected `.deps.json` target and compares actual managed, native, and resource file bytes with entries inside the exact cached package archives. It also checks TraceEvent's two x64 content DLLs and WPF satellite assemblies absent from these manifests, and rejects unaccounted DLL/EXE files. It does not load those libraries. The sole allowed facade substitution is the same-version WPF `WindowsBase.dll` replacing the core runtime facade when outputs are combined.
- Fetches **every** page advertised by NuGet's current [VulnerabilityInfo index](https://api.nuget.org/v3/vulnerabilities/index.json), matches ranges with `NuGet.Versioning`, and fails on advisory matches or inaccessible/malformed feeds. The [NuGet API contract](https://learn.microsoft.com/en-us/nuget/api/vulnerability-info) requires pages to be combined additively.
- Checks that bundled .NET runtime packs match the current patch in Microsoft's [.NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json).
- Records feed timestamps/digests, lock-file digests, package versions/content hashes, asset SHA-256 hashes, matches, and errors in JSON. Its regression check accepts original package bytes and rejects both a one-byte alteration and an unaccounted extra binary without executing the fixture DLL.

The baseline check used the previously existing bundle, not the newly remediated application. It covered **11 locks, 78 locked package/version pairs plus two runtime packs, and 435 asset declarations covering 434 distinct files**. The base feed was updated 2026-10-06 and its additive update 2026-10-12. The newly rebuilt candidate must pass separately. The baseline JSON and fixture results are in ignored `.artifacts/dependency-verification/`; they are not published diagnostic recordings.

## Native components and limitations

| Component | Resolved version | Review result |
|---|---|---|
| .NET core and WPF runtime packs | 10.0.12 | Matched archived assets; official metadata currently lists runtime 10.0.12 and SDK 10.0.401. |
| SQLite native package | SQLitePCLRaw.lib.e_sqlite3 3.53.3 | Native file matched this package, independently of wrapper 2.1.11. [CVE-2025-6965's NuGet advisory](https://github.com/advisories/GHSA-2m69-gcr7-jv3q) concerns the older native package range, not this resolved version. |
| SkiaSharp native assets | 2.88.9 | Matched archived bytes; the upstream [release policy](https://mono.github.io/SkiaSharp/docs/releases/index.html) marks SkiaSharp 2.x obsolete/unmaintained. Plan a compatible chart-stack upgrade before adding external images, fonts, or SVG input. |
| HarfBuzzSharp native assets | 7.3.0.3 | Matched archived bytes. Upstream HarfBuzz advisories require separate review because NuGet's package database does not describe every native component. |

For the current graphics path, source review found no demonstrated application trigger for the following upstream advisories:

- [GHSA-q4gc-p4hh-3765](https://github.com/harfbuzz/harfbuzz/security/advisories/GHSA-q4gc-p4hh-3765) affects HarfBuzz before 14.0.0 when a caller supplies a text offset beyond its buffer with an explicit zero item length. The exact package's [`SKShaper` string path](https://github.com/mono/SkiaSharp/blob/9699cdf046a989423a5e5eca68e7fd813486d81c/source/SkiaSharp.HarfBuzz/SkiaSharp.HarfBuzz.Shared/SKShaper.cs) uses whole strings; [`Buffer`](https://github.com/mono/SkiaSharp/blob/9699cdf046a989423a5e5eca68e7fd813486d81c/binding/HarfBuzzSharp.Shared/Buffer.cs) passes offset zero and length minus one for those overloads. The application does not supply custom offsets.
- [GHSA-p965-5rr7-9mhq](https://github.com/harfbuzz/harfbuzz/security/advisories/GHSA-p965-5rr7-9mhq) requires debug message callbacks and a crafted font; no callback setup or external font loader was found in the application. [CVE-2026-22693](https://github.com/harfbuzz/harfbuzz/security/advisories/GHSA-xvjr-f2r9-c7ww) involves font subsetting; no application subsetting path was found.
- [CVE-2024-56732](https://github.com/harfbuzz/harfbuzz/security/advisories/GHSA-qmp9-xqm5-jh6m) identifies the Cairo path in HarfBuzz 8.5.0–10.0.1, outside this package's 7.3 line.
- The upstream [libexpat update issue](https://github.com/mono/SkiaSharp/issues/3389) is a reason to track native maintenance. No application API for loading arbitrary SVG/images was found; the issue alone does not demonstrate an exploitable path in this application.

The exact ScottPlot package source [`LabelStyle`](https://github.com/ScottPlot/ScottPlot/blob/461efbdb139fb7c7d7a4bae185cde26e885b74bd/src/ScottPlot5/ScottPlot5/Primitives/LabelStyle.cs) defaults HarfBuzz/RTL shaping off, and the application never enables it. Imported process names can reach chart labels, so this is a source-grounded prerequisite assessment rather than treating all displayed text as trusted.

SQLite's [upstream change log](https://www.sqlite.org/changes.html) contains later 3.53.4 fixes and a 3.54.0 release. On this verification date, the native package's [NuGet version index](https://api.nuget.org/v3-flatcontainer/sqlitepclraw.lib.e_sqlite3/index.json) lists 3.53.3 as its latest stable version and 3.53.4.1-pre1 as a preview. No untested preview update was substituted for the pinned package.

These observations are not native parser fuzzing or a complete native-component SBOM. Runtime-pack publisher signatures were not independently validated. Re-run advisory and asset verification for each new release; revisit the graphics assessment whenever rendering inputs or dependencies change.
