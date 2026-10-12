# Release remediation — 13 October 2026

**Verdict: locally validated engineering-preview candidate; stable executable release remains gated.** Storage and package-privacy changes are implemented. Current source, exported source and extracted application were checked locally on Windows 10 build 19045 x64. No elevated capture or privileged filesystem attack was performed. No commit, push, tag or GitHub release was created by this remediation.

## What changed

- Collector scratch and recordings move from user-writable AppData to an ACL-protected, per-user subtree on the helper application's fixed local NTFS volume. Installing on D: puts this collector storage on D:. Existing recordings are preserved; legacy metadata is excluded from privileged recovery.
- Directory ownership/ACLs and reparse attributes are validated using native handles. Directory-list handles pin the root and ancestors against replacement. Untrusted ancestors deny write sharing; protected directories allow writer sharing for atomic file publication. Writes use the opened volume's GUID path. Profiles and random pending metadata use exclusive creation; metadata is flushed before replacement. Reads reject redirects and multiple hard links. Completed file size and SHA-256 use the same validated handle.
- Administrators and SYSTEM retain full control; the signed-in user has read access to their subtree. OWNER RIGHTS inheritance removes implicit permission-changing rights from ordinary file owners. Save recording copy remains an unprivileged operation. Start requires 2 GiB free; WPR receives protected TEMP/TMP/SystemTemp values. Service-side writes have not been measured.
- Source and application packagers share private-file exclusions and reject redirected paths. Tracked private files are excluded independently of Git ignore rules. Untracked documents are omitted unless explicitly required as reviewed public docs; new nonignored source/build files remain exportable. Application publication omits developer PDBs and rejects private filenames in staging.
- Publication verifies the current dependency graph, canonical package content hashes, actual shipped asset bytes, known NuGet advisories and the .NET runtime patch before promoting a bundle. CI includes the dependency tampering regression.

## Executed checks

| Check | Result |
|---|---|
| Cached locked restore and Release solution build | PASS; zero warnings/errors; no dependency downloads |
| Full test suite, including real ETLs | PASS; 113 passed, zero failed/skipped |
| Storage regressions | PASS; unsafe-existing-root refusal without mutation, root/ancestor rename exclusion, volume identity, atomic child replacement, exclusive profile creation and held read-lease exclusion |
| Source/application privacy regression | PASS; 34 private filenames, mixed case, forcibly tracked dummy secrets, untracked-doc omission, new-source retention, no-Git fallback and junction rejection |
| Dependency fixture | PASS; original bytes accepted; one-byte change and undeclared binary rejected |
| Fresh bundle and extracted ZIP dependency checks | PASS; 11 locks, 78 locked package/version pairs plus two runtime packs; 435 asset declarations/434 distinct files; zero known NuGet advisory matches; runtime 10.0.12 current in retrieved metadata |
| Application ZIP content and extraction | PASS; 573 files byte-identical to the published folder, zero private filename matches, zero PDBs; reviewed dependency documentation present |
| Extracted desktop workflow | PASS; lazy analysis startup, saved-only startup, worker recovery, native observation, investigation and saved comparison |
| Extracted packaged worker replay | PASS; ten iterations, 228 requests, 50,000 paged events, two restarts; original fingerprints preserved; damaged import, unsupported requests, cancellation and crash recovery checked |
| Replay measurements | Contributor p95 116.72 ms; first-page p95 44.50 ms; peak worker working set 357.62 MiB. Shared-machine fixture measurements, not maximum-envelope or whole-product certification |
| Source ZIP | PASS; 236 files, new storage/dependency scripts included; independently restored from the existing cache and built with zero warnings/errors |
| Whitespace check | PASS |

An initial regression showed that metadata-only directory handles did not prevent rename. Directory-list access corrected that, but denying write sharing on protected directories then blocked atomic replacement. The final correction retains ancestor exclusion and uses validated protected ACLs to deny ordinary-user writes while permitting legitimate child-file replacement. Those failures were corrected before the passing full suite. An independent focused source reread found no definite remaining storage bypass; this is not native exploit validation.

Commands and raw results are retained locally under ignored `.artifacts/release-remediation/`. Restore used an empty local feed with `--locked-mode -p:NuGetAudit=false`; the separate dependency verifier then fetched current public advisory JSON. Publication used `publish.ps1 -NoRestore`. Raw TRX, desktop evidence and local diagnostic results may contain machine metadata and must not be attached to a public release without review.

## Candidate identity

Base commit: `dd2240feffacd5c7114801baa337e6a13682cc84`, with the remediation present as uncommitted working-tree changes. The archives identify this candidate; the base commit alone does not.

| Archive | SHA-256 |
|---|---|
| WindowsOpenIntelligence-win-x64.zip | `6698d8341bb19af987faeb83f683a18af7a8d68a50a7e2aed5d74977db40f273` |
| WindowsOpenIntelligence-source.zip | `cc39331c0d64da7d91399c5b53a72ec3a72c69a8c8a4b23a5f7d18cda6659fb4` |

Archives and checksum files are in `dist/`. This later, untracked report is deliberately outside those archives; their tracked validation document and explicitly reviewed dependency report describe the implemented changes. Rebuilding changes archive hashes and requires renewed identity verification.

## Remaining release gates

1. **Native capture/storage acceptance:** use an isolated Windows 11 environment with administrator and medium tokens of the same dummy account. Test attempts to write files, change ACLs, rename ancestors and set junctions; assert unrelated canaries remain unchanged. Run both shipping profiles through start/save/import/report and deadline/cancel/disconnect/crash-recovery/coexistence. Verify volume-GUID path compatibility, WPR-created ETL permissions and temporary merge files. Earlier server runs predate this implementation.
2. **Helper trust:** review how users obtain and verify the portable elevated executable, including signing/provenance. This candidate is not signed or backed by exact-revision remote CI.
3. **Native dependency maintenance:** [the dependency review](dependencies-2026-10-13.md) records unmaintained SkiaSharp 2.x and the separate HarfBuzz prerequisite assessment. Zero NuGet advisory matches is not a claim that every embedded native component is vulnerability-free. Plan a compatible maintained graphics-stack upgrade; revisit reachability when rendering inputs change.
4. **Broader acceptance:** clean Windows 11 launch/use, rich/maximum-size trace behavior, long-run UI/observer cost, service-side physical writes, workload slowdown and sleep/resume remain separate existing gates. This short remediation run does not close them.

Privileged attack validation requires an isolated OS-enforced Windows environment with controlled accounts, scratch-only writes, no external network and resource limits. This host does not provide the complete audit sandbox. Ordinary cached builds, regressions and packaged workflow checks are host validation, not privileged security proof. Preserve `NEEDS_VALIDATION` for those gates.
