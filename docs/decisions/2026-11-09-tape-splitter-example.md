# A tape splitter example, its first slice only (2026-11-09)

Issue #23 proposed an example that takes one long capture of a tape (VHS, DV, DVD) and proposes where to cut it: it
finds the blank stretches (black, a blue "no signal" screen, a frozen last frame, noise) and then writes each recording
as a file of its own by stream copy. FrameReader has the detectors, each checked against FFmpeg (`PictureMeter`'s
black, `ConstantColourDetector`, `FreezeDetector`, `SilenceFinder`); it lacks a trim (`Remux.Copy` with a start and
an end), a measure for analogue noise, and rules from spans to cut points.

## Question

Where does it live, and how much of it is built now?

## Choices

1. **An example in this repository** (examples/, beside ThumbnailApi): a showcase of the detectors, built on the
   public API only.
2. **A ShotDetector command** (`shotdetect`): next to the cut detection a recording that starts with no gap needs.
3. **Neither**: LosslessCut and dvrescue may cover it.

And for the scope: the whole idea (report, trim, noise), or the issue's first slice: a report of the blank spans and
the proposed cut points as JSON, on media generated with lavfi, writing nothing.

## Answer

**An example here, the first slice only** (the owner, 2026-11-09). examples/TapeSplitter reports the black, flat
colour, frozen and silent spans, the gaps and the cut points as JSON, with the existing detectors; its tests compare
the spans with blackdetect, freezedetect and silencedetect on tapes made with lavfi. It writes no files: no trim with
stream copy and no noise measure, each a later step needing its own decision (docs/backlog.md).

What the other tools do (checked 2026-11-09, in examples/TapeSplitter/README.md): LosslessCut detects black scenes
and silences with ffmpeg's blackdetect and silencedetect and makes segments of them, but not a blue screen or a
frozen picture; dvrescue's dvpackager splits DV only, by its recording timestamps and timecode. So the example shows
the detectors together in one decode, rather than filling a gap no tool covers.

Since 2026-11-13 ([2026-11-13-tape-splitter-trim.md](2026-11-13-tape-splitter-trim.md)) the example also writes each
recording, by stream copy, with `Remux.Copy`'s new `Start` and `End`.
