### New

- FrameReader.Native: the raw `dv` muxer, so `Remux.Copy` writes .dv files (a DV capture trimmed or copied as .dv,
  DV from an AVI into .dv), the file `ffmpeg -c copy` writes. Before, "No muxer for ..." with our libraries.

### Fixed

- FrameReader.Native: zlib 1.4 (was 1.3.2), linked into FFmpeg's libraries.
- FrameReader.Native: the SBOM's `documentNamespace` names this repository (was ShotDetector's).
