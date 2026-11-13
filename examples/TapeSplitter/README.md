# TapeSplitter: where a digitised tape's recordings start and end

A console app around FrameReader ([Program.cs](Program.cs), [TapeScan.cs](TapeScan.cs)). People and digitisation
services capture a whole VHS, DV or DVD tape as one file; its ends and the gaps between recordings are blank. This
reads such a capture once and reports, as JSON, the blank stretches it found and where to cut it; with `--write`, it
also writes each recording as a file of its own, by stream copy (issue #23).

```bash
FrameReader__Libraries=/path/to/ffmpeg-8-shared/lib dotnet run --project examples/TapeSplitter -- capture.dv
```

Options: `--min-gap 2` (the shortest blank stretch, seconds), `--silence -60` (the silence level, dB), `--write
recordings` (write each recording into that folder as `recording-01.dv` and on, the capture's extension).

```json
{
  "duration": 23,
  "spans": [
    { "kind": "black", "start": 0, "end": 2 },
    { "kind": "flat", "start": 0, "end": 2, "colour": [16, 128, 128] },
    { "kind": "frozen", "start": 0, "end": 2 },
    { "kind": "silence", "start": 0, "end": 2.000021 },
    { "kind": "flat", "start": 6, "end": 9, "colour": [49, 212, 114] },
    { "kind": "frozen", "start": 12.96, "end": 16 },
    ...
  ],
  "gaps": [
    { "start": 0, "end": 2, "kinds": ["black", "flat", "frozen"], "silence": 2 },
    { "start": 6, "end": 9, "kinds": ["flat", "frozen"], "silence": 3 },
    { "start": 12.96, "end": 16, "kinds": ["frozen"], "silence": 3 },
    { "start": 20, "end": 23, "kinds": ["black", "flat", "frozen"], "silence": 3 }
  ],
  "cuts": [2, 6, 9, 12.96, 16, 20],
  "recordings": [{ "start": 2, "end": 6 }, { "start": 9, "end": 12.96 }, { "start": 16, "end": 20 }]
}
```

(The tape its tests make, as DV: black leader, a recording, a blue screen, a recording that freezes on its last frame,
a recording, black to the end.)

## What it finds

The picture is decoded once and fed to three detectors, the sound once to a fourth. Each gives what the FFmpeg filter
it reproduces gives, and the tests (tests/TapeSplitter.Tests) check that on tapes made with lavfi, as a lossless
capture (FFV1, PCM), as DV, and at 10 bits 4:2:2 without sound:

| Kind | Detector | As ffmpeg's |
|---|---|---|
| `black` | `PictureMeter` (`PictureMeasures.BlackSpans`) | `blackdetect` (its defaults but `d`) |
| `flat` | `ConstantColourDetector`: one flat colour, a VCR's blue screen; `colour` is its mean Y, Cb, Cr in 8-bit codes | no filter: signalstats' figures |
| `frozen` | `FreezeDetector` | `freezedetect` (its defaults but `d`) |
| `silence` | `SilenceFinder` | `silencedetect` on the samples as floats (`aformat=flt`) |

Times are seconds on the stream's own clock (ffmpeg's `-copyts`). A span's end is the detector's: blackdetect ends a
span that runs to the end at the last frame (a FIXME in FFmpeg), freezedetect gives none (`null`).

## From spans to cuts

- A **gap** is where the picture is blank: black, flat or frozen spans that overlap or touch, merged; one running to
  the end runs to the video's end. `silence` says how many of its seconds the sound is silent too; silence alone makes
  no gap (a quiet scene isn't one).
- The **cuts** are the gaps' edges, the **recordings** what lies between them. A frozen picture's gap starts at its
  first frame (freezedetect's start), which is the recording's last.
- `--min-gap` is every detector's minimum duration (`d`), 2 s by default, their default.

These rules are deliberately the simplest; better ones (how long a gap must be, whether picture and sound must agree)
are a later step.

## Writing the recordings

`--write` (`TapeScan.Write`) copies each recording with `Remux.Copy`, `Start` and `End` at its edges: the file
`ffmpeg -ss START -to END -i capture -c copy recording` writes, which the tests check byte for byte on the DV and FFV1
tapes. A stream copy starts at a keyframe, so a recording is exact only where every frame is one:

- **DV, MJPEG, FFV1 with `-g 1`** (as archives encode it): exact, the recording's frames and no others.
- **FFV1 by default**: a keyframe every 12 frames (FFV1's frames between reuse the coder's state), so a recording
  starts up to 11 frames early.
- **MPEG-2 (DVD) and H.264**: from the keyframe at or before the start, up to a GOP early (half a second on a DVD);
  the frames before have negative timestamps (an .mp4 hides them with an edit list, other players may show them).

The report's times are on the stream's clock; `-ss` and `-to` count from the container's start time, so `Write`
subtracts `MediaProbe.Probe(path).StartTime` (0 but for MPEG-TS and the like).

## What it doesn't do (yet)

- **Analogue noise** ("snow" on a VHS tape's unrecorded stretches): no FFmpeg filter names it, so it would be
  FrameReader's own measure, outside the promise that every figure is checked against ffmpeg. The owner's decision.
- **A recording that starts with no gap** (a hard cut): cut detection (ShotDetector) or DV's recording date.

## What already does this

Checked 2026-11-09 before building it:

- [LosslessCut](https://github.com/mifi/lossless-cut) detects black scenes, silences and scene changes (ffmpeg's
  blackdetect, silencedetect and scene score) and makes segments of them, at a section's centre or around it, then
  cuts losslessly. It has no blue-screen or frozen-picture detection.
- [dvrescue](https://mipops.github.io/dvrescue/)'s `dvpackager` splits DV captures at recording-start markers,
  discontinuous recording timestamps (`-d`) or timecode (`-t`), from the DV stream's own metadata: DV only, and it
  finds no blank stretches.

So for black and silence LosslessCut covers it; this example shows FrameReader's detectors together in one decode,
with the blue screen and the frozen picture that a VCR's output adds, from .NET and in-process.

In your own app, reference the packages instead of the project:

```bash
dotnet add package FrameReader
dotnet add package FrameReader.Native.linux-x64   # or FrameReader.Native: every platform
```
