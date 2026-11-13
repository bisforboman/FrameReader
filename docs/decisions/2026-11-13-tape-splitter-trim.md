# The tape splitter's next step: the trim (2026-11-13)

Issue #23's first slice (examples/TapeSplitter, a report that writes nothing) was in PR #24, its checks green, not
merged. What was left of the issue: a trim with stream copy, a measure for analogue noise, and the rules from spans to
cut points (docs/decisions/2026-11-09-tape-splitter-example.md).

## Question

What does this session do for #23?

## Choices

1. **Merge #24, then the trim**: `Remux.Copy` with a start and an end (ffmpeg's `-ss S -to E -i IN -c copy`, checked
   against ffmpeg's file), so the splitter can write each recording. A library change, so a minor release.
2. **Only land #24.**
3. **Merge #24, then the noise measure**: FrameReader's own figure for "snow", not checked against ffmpeg.
4. **Review #24 first.**

## Answer

**Merge #24, then the trim** (the owner, 2026-11-13). `RemuxOptions.Start` and `End` are ffmpeg's input `-ss` and
`-to`: the same seek (to the keyframe at or before, 3/23 s earlier with B-frames), the same timestamp offset, each
stream ended by its estimated decoding time as fftools' demuxer does, chapters moved as ffmpeg moves them; the tests
compare the files byte for byte. They count from the container's start time, as `-ss` does, so `MediaInfo.StartTime`
(ffprobe's `format=start_time`) came too, for the splitter, whose times are on the stream's clock. examples/TapeSplitter
writes each recording with them (`--write`).

Found on the way: FFV1 is intra-only only with `-g 1` (by default a keyframe every 12 frames, the frames between
reusing the coder's state), so a cut of a default FFV1 capture starts up to 11 frames early, as ffmpeg's does. Archives
encode FFV1 with `-g 1`; the docs say so.

The noise measure and the rules from spans to cut points stay Maybe rows in docs/backlog.md.
