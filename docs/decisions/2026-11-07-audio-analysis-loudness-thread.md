# AudioAnalysis: the loudness graph on its own thread (2026-11-07)

A performance review of `AudioAnalysis.Run` found the loudness graph (`ebur128=peak=true+sample`, whose true peak
upsamples to 192 kHz inside the filter) to be most of a pass that also draws a waveform or looks for tone or silences:
on one thread the pass takes the graph's time plus the decode, the conversion and the other analyses'. The 2026-11-03
design had kept everything on one thread, judging the analyses' work per chunk small next to decoding.

## Question

Should `AudioAnalysis.Run` use a second core, and how?

## Choices

1. **The loudness graph on a worker thread** (recommended): each decoded frame is handed as a reference
   (`av_frame_ref`, no copy) with its skip and position through a bounded queue to a thread that feeds and drains the
   graph, while the calling thread converts and runs the others. Each consumer sees the same frames in the same
   order, so every result stays the same, to the bit. Wall time goes from the sum of the two paths to the slower one.
2. **Also decode ahead** (as `ReadAhead` does for video): a third thread; decoding is a small part of an audio pass,
   so little to gain for the extra machinery.
3. **Leave it on one thread.**

## Answer

**Choice 1** (the owner, 2026-11-07, from the performance review). Settled in the code (`LoudnessThread`):

- Only when loudness runs with another analysis (or `Chunks`): loudness alone has no conversion to overlap with and
  stays on the calling thread.
- The queue holds at most 16 frames (`LoudnessThread.Depth`), so memory stays a few frames of audio; the caller waits
  for room.
- What the graph throws is captured on the worker and thrown on the calling thread (the same exception, so the same
  types as before) at its next frame or at the end; the pass stops.
- The `CancellationToken` stops both sides between frames, and wakes a caller waiting for room. Dispose (on every
  path, a failure, the host's callback throwing, a cancellation) stops the thread after its current frame, joins it and
  lets go of the frames left. `AudioAnalysisTests` checks a failure and a cancellation mid-run on the worker, and that
  no frame or thread is left after.
- Progress is reported from the calling thread, as before. The host's `Chunks` callback runs on the calling thread.
- Draining the graph no longer copies its (thrown away) output out: `AudioFilter.Drain`.
