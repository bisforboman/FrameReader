# TapeSplitter: where a digitised tape's recordings start and end

A console app around FrameReader ([Program.cs](Program.cs), [TapeScan.cs](TapeScan.cs)). People and digitisation
services capture a whole VHS, DV or DVD tape as one file; its ends and the gaps between recordings are blank. This
reads such a capture once and reports, as JSON, the blank stretches it found and where to cut it; with `--write`, it
also writes each recording as a file of its own, by stream copy (issue #23).

```bash
FrameReader__Libraries=/path/to/ffmpeg-8-shared/lib dotnet run --project examples/TapeSplitter -- capture.dv
```

Options: the rules and thresholds [below](#from-spans-to-cuts) (`--min-gap 2`, `--sound agree`, ...), `--write
recordings` (write each recording into that folder as `recording-01.dv` and on, the capture's extension), `--as avi`
(their container instead: FrameReader.Native's FFmpeg has no raw `dv` muxer, so write a .dv capture's recordings as
`avi` or `mkv`, the DV frames unchanged).

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
| `black` | `PictureMeter` (`PictureMeasures.BlackSpans`) | `blackdetect` (`d`, `pix_th`, `pic_th`) |
| `flat` | `ConstantColourDetector`: one flat colour, a VCR's blue screen; `colour` is its mean Y, Cb, Cr in 8-bit codes | no filter: signalstats' figures |
| `frozen` | `FreezeDetector` | `freezedetect` (`d`, `n`) |
| `silence` | `SilenceFinder` | `silencedetect` (`d`, `n`) on the samples as floats (`aformat=flt`) |

Times are seconds on the stream's own clock (ffmpeg's `-copyts`). A span's end is the detector's: blackdetect ends a
span that runs to the end at the last frame (a FIXME in FFmpeg), freezedetect gives none (`null`).

## From spans to cuts

The rules (`TapeScanOptions`; docs/decisions/2026-11-15-tape-splitter-cut-rules.md), in the order they apply:

1. **Spans**: each detector reports the stretches of at least `--min-span` (0.5 s), with its thresholds.
2. **Gaps**: the picture's spans (black, flat, frozen) that overlap or touch are merged, and a merged stretch is a gap
   when it lasts `--min-gap` (2 s). So a second of black and the blue screen after it are one gap, though neither
   lasts 2 s. One running to the end runs to the video's end. A frozen picture's gap starts at its first frame
   (freezedetect's start), which is the recording's last.
3. **Sound**: `silence` says how many of a gap's seconds the sound is silent too. With `--sound agree` a gap is only
   the part where the sound is silent as well, kept when that still lasts `--min-gap` (less one frame: the picture's
   clock ticks in frames, the sound's in samples): the cut moves to where the sound stops, and a still with music is
   no gap. Silence alone never makes a gap (a quiet scene isn't one), and a capture without sound is the picture's
   to decide.
4. **Glitches**: a stretch between two gaps, or between a gap and the tape's start or end, shorter than
   `--min-recording` (1 s) is no recording but part of the gap (the frames a VCR shows between black and its blue
   screen).
5. The **cuts** are the gaps' edges, the **recordings** what lies between them.

| Option | Default | What it is | Why that default |
|---|---|---|---|
| `--min-gap` | 2 | The shortest gap, seconds | blackdetect's, freezedetect's and silencedetect's own `d` |
| `--min-span` | 0.5 | Each detector's `d` (the flat colour's duration), seconds; at most `--min-gap` | Short enough for a VCR's second of black before its blue screen; a short span alone makes no gap |
| `--min-recording` | 1 | The shortest recording, seconds; 0 keeps every stretch | Nobody records for under a second; a VCR's switch takes a few frames |
| `--sound` | `ignore` | `ignore`: the picture alone decides. `agree`: the sound must be silent too | Tapes hum and hiss through their blank stretches, so asking for silence would miss gaps (the owner, 2026-11-15) |
| `--kinds` | `black,flat,frozen` | Which picture detectors run and make gaps | All; leave `frozen` out for a tape of stills (slides, title cards) |
| `--silence` | -60 | silencedetect's `n`, dB | Its default. An analogue capture's noise floor is higher: try -40 with `--sound agree` |
| `--black` | 0.10 | blackdetect's `pix_th`: how dark a black pixel is | Its default. Raise it for a capture whose black is lifted |
| `--black-share` | 0.98 | blackdetect's `pic_th`: the share of black pixels in a black frame | Its default. Lower it for a capture with head-switching noise at the bottom |
| `--freeze-noise` | 0.001 | freezedetect's `n`: how much a frame may differ and still be the same picture | Its default (-60 dB). An analogue still is noisy: raise it |
| `--flat-spread` | 4 | `ConstantColourOptions.Spread`: how flat a flat colour is, 8-bit codes | Its default |

The tests run each rule on a second tape (a short black before a blue screen, a still with music, a glitch, black
with the sound going on, a noisy still, dark grey) and compare the spans with the filters given the same options.

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

## What it doesn't do

- **A recording that starts with no gap** (a hard cut, one recording straight after another). Finding a cut in the
  picture is cut detection, [ShotDetector](https://github.com/bisforboman/shotdetector)'s work: no cut detector is
  built here. A DV tape carries each frame's recording date and time, and a jump in it is a new recording, but
  FFmpeg 8.1 doesn't read it (its `dv` demuxer gives the first frame's timecode and nothing per frame; ffprobe, the
  decoder and the filters show none), so there is no figure of ffmpeg's to check one against, and it is left out (the
  owner, 2026-11-15). For DV, [dvrescue](https://mipops.github.io/dvrescue/)'s `dvpackager` splits by it.
- **Analogue noise** ("snow" on a VHS tape's unrecorded stretches): no FFmpeg filter names it, so it would be
  FrameReader's own measure, outside the promise that every figure is checked against ffmpeg. Not approved; a Maybe
  row in docs/backlog.md.

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
