# Security: what we do, and how to check it

For a team deciding whether to adopt FrameReader: what keeps the code, its dependencies and the published packages
safe, each with a place to check it without taking our word for it. How to report a vulnerability, which versions get
fixes and how fast: [SECURITY.md](../SECURITY.md). Using FrameReader safely on untrusted input (protocols, TLS,
memory limits): the package README's
[Security section](../src/FrameReader/README.md#security).

## At a glance

- **OpenSSF Scorecard**: https://scorecard.dev/viewer/?uri=github.com/bisforboman/FrameReader, an independent score
  of the repository's practices (branch protection, pinned dependencies, token permissions, signed releases, code
  review, static analysis, a security policy), refreshed weekly and on each push to main (scorecard.yml).
- **Advisories**: https://github.com/bisforboman/FrameReader/security/advisories, every vulnerability fixed, with its
  CVE and fixed version. They also reach NuGet's audit, so your `dotnet restore` warns about an affected version.
- **Releases**: [the changelog](../src/FrameReader/CHANGELOG.md) and
  https://github.com/bisforboman/FrameReader/releases, each with its packages, SBOMs and attestations.

## Verifying a package

Every package on nuget.org was built by `release.yml` in this repository from a tagged commit, and nothing else
pushes to nuget.org: publishing uses NuGet's trusted publishing (a key valid for an hour, issued to that workflow
only; no stored API key). To check one you downloaded:

```bash
gh attestation verify FrameReader.2.27.0.nupkg --repo bisforboman/FrameReader      # built here, from which commit
gh attestation verify FrameReader.2.27.0.nupkg --repo bisforboman/FrameReader \
  --predicate-type https://spdx.dev/Document/v2.2                                   # its SBOM, signed
```

- **Provenance**: a signed build attestation (Sigstore, through GitHub) for every `.nupkg` and `.snupkg`, naming the
  workflow, commit and run.
- **SBOMs** (SPDX): `FrameReader`'s, inside the package (`_manifest/spdx_2.2/manifest.spdx.json`) and on the GitHub
  release, with a signed attestation; each `FrameReader.Native.<rid>`'s (`sbom.spdx.json`: FFmpeg and what's linked
  into it, with versions, source URLs, SHA-256, licences and CPEs), inside the package and on the release.
- **Reproducible from source**: deterministic builds, SourceLink to the exact commit and symbol packages, so a
  debugger steps into the source the package was built from. Our FFmpeg build's recipe is in each native package
  (`configure.txt`: sources, their SHA-256 and the configure line) and in
  [tools/native/build-ffmpeg.sh](../tools/native/build-ffmpeg.sh).

## Keeping dependencies safe

- **NuGet packages**: a package with a known vulnerability, direct or transitive, fails every restore and so every
  build, test, pack and release (NuGet's audit, all severities, as errors:
  [decision](decisions/2026-11-06-vulnerable-packages.md)). A weekly run (vulnerable-packages.yml) catches an
  advisory published against what main already ships, and Dependabot opens updates (security ones at once, the rest
  weekly).
- **FFmpeg and the native libraries**: our own build, from pinned, checksummed sources, with only the decoders,
  demuxers, filters and protocols FrameReader uses (less code that untrusted input can reach). Each week
  native-cves.yml looks up the exact versions in the NVD and opens an issue per CVE that affects one, and upstream.yml
  opens one per new release of FFmpeg, zlib, dav1d or OpenSSL; [SECURITY.md](../SECURITY.md) says how fast a fix
  ships.
- **GitHub Actions**: every external action pinned to a commit SHA (Dependabot moves the pins weekly), every
  workflow's token read-only unless a job needs more, and the release job runs only our code and our FFmpeg build:
  the tests, which use a third-party ffmpeg, run in a job without the publishing credentials.

## Checking our code

- **CodeQL** on every pull request, every push to main and weekly (codeql.yml; findings under Security, Code
  scanning).
- **The .NET SDK's security analyzers** (CA2xxx, CA3xxx, CA5xxx) as errors on every build
  ([decision](decisions/2026-11-06-security-analyzers.md)).
- **Review**: main is protected; every change goes through a pull request and CI, which compares FrameReader's
  output with ffmpeg's byte for byte.
- **Security reviews** of the code, the native build and the workflows: findings and fixes are recorded in
  docs/decisions/ (and, before the move, ShotDetector's, such as its 2026-10-16 review).
