# Security policy

FrameReader decodes media, often media someone else made, inside your process. This is how vulnerabilities in it, and
in the FFmpeg build its native packages ship, are reported, fixed and announced. What protects the code and the
packages, and how to check it yourself: [docs/security.md](docs/security.md).

## Supported versions

Security fixes ship as a patch release of the newest minor version (`X.Y.Z+1`); earlier minors don't get backports,
so stay on the newest minor of the major you use (a minor never breaks the API: package validation fails a release
that would). `FrameReader` and `FrameReader.Native.<rid>` (and the `FrameReader.Native` meta-package) are released
together under one version.

| Version | Fixes |
| --- | --- |
| The newest minor (see [the changelog](src/FrameReader/CHANGELOG.md)) | Yes |
| Older minors and majors | No: move to the newest |

## Reporting a vulnerability

Please don't open a public issue. Report it privately through GitHub: the repository's **Security** tab, **Report a
vulnerability** ([direct link](https://github.com/bisforboman/FrameReader/security/advisories/new)). Say what's
affected (FrameReader's code, or the FFmpeg build in FrameReader.Native, and which version and platform), how to
reproduce it (a file that triggers it helps), and what an attacker gains.

A bug in FFmpeg itself also goes to FFmpeg (https://ffmpeg.org/security.html); we follow their fix with a release here.

## What happens then

- **Within 7 days**: an answer that the report arrived, and a first assessment.
- **Within 30 days** for a high or critical vulnerability (CVSS 7.0 or more), **90 days** for the others: a release
  with the fix. A vulnerability in a library we ship (FFmpeg, zlib, dav1d, LAME, libwebp, OpenSSL) counts from the
  day upstream has a fix (a release, or a patch we can apply).
- **With the release**: a GitHub security advisory with a CVE (GitHub assigns one), the fixed version and credit to
  the reporter unless they'd rather not. GitHub's advisory database feeds NuGet's audit, so `dotnet restore` warns
  every project that still uses an affected version. The CHANGELOG entry names the CVE.

Until the advisory is published, the report stays between the reporter and the maintainer.

## Native libraries

FrameReader.Native ships FFmpeg with zlib, dav1d, LAME and libwebp linked in (and OpenSSL on Linux). Each week
`native-cves.yml` looks up those exact versions in the NVD: every CVE that affects one is listed in the open issue
"Native CVEs (FrameReader.Native)", and each high or critical one whose affected versions the NVD records gets an
issue of its own. `upstream.yml` opens an issue for each new release of them. A CVE that reaches FrameReader (our
build leaves out most of FFmpeg's decoders, filters and protocols) is fixed within the times above. Each native package
carries an SPDX SBOM (`sbom.spdx.json`) of what's inside, with CPEs, for your own scanners.
