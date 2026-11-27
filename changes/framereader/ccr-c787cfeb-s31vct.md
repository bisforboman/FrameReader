### New

- A security policy ([SECURITY.md](https://github.com/bisforboman/FrameReader/blob/main/SECURITY.md)): which versions
  get fixes, how to report a vulnerability privately, and how fast fixes ship (high and critical within 30 days).
  What protects the packages and how to check it: docs/security.md.
- The `FrameReader` package carries an SPDX SBOM (`_manifest/spdx_2.2/manifest.spdx.json`), and every GitHub release
  attaches the SBOMs of `FrameReader` and of each `FrameReader.Native.<rid>`, `FrameReader`'s with a signed
  attestation (`gh attestation verify <nupkg> --repo bisforboman/FrameReader --predicate-type https://spdx.dev/Document/v2.2`).
