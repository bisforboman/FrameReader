# The tape splitter's last piece: the rules from spans to cut points (2026-11-15)

examples/TapeSplitter reported the blank spans, gaps and cut points (PR #24) and wrote each recording by stream copy
(PR #26). Left of issue #23, both Maybe rows in docs/backlog.md: a measure for analogue noise ("snow"), and the rules
from spans to cut points with cut points for recordings that start with no gap.

## Question 1: what finishes issue #23?

Choices: **Cut-point rules**, **Noise measure**, **Both**, **Close it as done**.

Answer: **Cut-point rules** (the owner, 2026-11-15). The noise measure is not approved: it stays a Maybe row, and the
issue closes with it there.

## Question 2: cuts at DV's recording date?

A recording that starts with no gap can be found on a DV tape by a jump in the recording date each frame carries
(dvrescue's dvpackager splits that way). Found: FFmpeg 8.1 doesn't read it. Its `dv` demuxer (libavformat/dv.c) reads
the first frame's timecode as a tag and nothing else; ffprobe's frames and packets, the decoder and the filters show
no date. Its muxer (dvenc.c) does write the date and time into every frame, from `-metadata creation_time`. So the only
check possible is a round trip: what FrameReader would read equals what ffmpeg was told to write, ffmpeg's writing
rather than its reading. FrameReader also has no public way to a packet's bytes, so it would be a library addition.

Choices:

1. **Leave DV dates out**: only the rules; the README says a hard cut needs dvrescue (DV) or ShotDetector, and why.
2. **Build it, with the round-trip check**: a library addition (a minor version) reading each DV frame's date.
3. **A backlog row, decide later.**

Answer: **Leave DV dates out** (the owner, 2026-11-15). No library change, and no backlog row. General cut detection
is ShotDetector's: the example builds no cut detector.

## Question 3: must picture and sound agree, by default?

Choices:

1. **Picture alone**: as before, a blank picture is a gap whatever the sound (tapes hum and hiss through blank
   stretches, so asking for silence would miss gaps); `--sound agree` is the option.
2. **Sound must agree**: a gap is only where the sound is silent too; `--sound ignore` is the option.

Answer: **Picture alone** (the owner, 2026-11-15).

## The rest, picked without asking

In examples/TapeSplitter/README.md, each with its why:

- Detectors report spans from 0.5 s (`--min-span`); merged, they are a gap from 2 s (`--min-gap`, the filters' own
  default `d`). Before, every detector's minimum was the gap's, so a second of black before a blue screen split or
  hid a gap.
- A stretch under 1 s between gaps, or at the tape's start or end, is part of the gap (`--min-recording`).
- With `--sound agree` a gap is the blank picture's silent part, kept when it lasts `--min-gap` less one frame.
- `--kinds` picks the picture detectors; the thresholds are the filters' options with the filters' defaults
  (`--silence`, `--black`, `--black-share`, `--freeze-noise`) and the flat colour's spread (`--flat-spread`).

The tests compare every set of options' spans with blackdetect, freezedetect and silencedetect given the same
options, on a tape made with lavfi; the gaps are what the tape was made of.
