# The dv muxer with the zlib 1.4 rebuild (2026-11-15)

PR #22 moves the native build to zlib 1.4 (issue #18). It edits tools/native/build-ffmpeg.sh, so every platform's
FFmpeg is rebuilt. The backlog had a Maybe row waiting for exactly that: the raw `dv` muxer, without which
`Remux.Copy` can't write .dv files with FrameReader.Native ("No muxer for ..."), so the tape splitter sends a .dv
capture's recordings into AVI or Matroska (found by PR #26's arm job).

## Question

PR #22 rebuilds every platform's FFmpeg anyway. Does the `dv` muxer go in with it?

## Choices

1. **zlib and the dv muxer**: `--enable-muxer=dv` in the same PR, with a test of `Remux.Copy` to .dv against ffmpeg's
   file. One rebuild for both; the next release is then a minor one (something new), not a patch.
2. **zlib only**: the PR stays what issue #18 asks for; the muxer waits for another rebuild.

## Answer

**zlib and the dv muxer** (the owner, 2026-11-15). RemuxTests' trim rows cover DV to .dv (trimmed and whole), .dv into
AVI and AVI into .dv, byte for byte against `ffmpeg -c copy`; where the libraries have no `dv` muxer those rows do
nothing and FFmpegLibrariesTests fails once, saying so (CI's in-process jobs all run on the build this script makes;
BtbN's has the muxer).

examples/TapeSplitter and its tests are not touched here: issue #23's session was working on them at the same time.
That the splitter's docs and test still say there is no `dv` muxer is a row in docs/backlog.md.
