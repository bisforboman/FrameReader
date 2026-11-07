# Performance work after a review of the read paths (2026-11-07)

The owner asked what could make FrameReader faster. A review that day read the video path (FrameDecoder,
VideoFrameReader, ReadAhead, BgrConverter), the audio path (AudioReader, AudioFilter, AudioAnalysis) and the managed
measures, and timed FrameReader against the ffmpeg 8.1 command line on generated 1080p H.264 in a 4-core cloud
container. Every idea had to keep the bytes (or the doubles) identical, or be opt-in.

## What the review found

- **Already where it should be.** A plain `TryRead` loop costs about what `ffmpeg -i X -f null -` does (decoding) and
  less than ffmpeg's rawvideo pipe, allocating next to nothing per frame. Contexts and graphs are made once, decoded
  frames are references, `thread_count` is ffmpeg's auto, the demuxer keeps ffmpeg's probe defaults (they decide the
  timestamps). The large wins already landed: frames nothing references left out (2.15.0 to 2.17.0), `ReadAhead`,
  the mp4 sample-table scan, audio reads that discard the other streams.
- **Rotation.** `FrameDecoder`'s own rotation (display rotation 90 or 270, phone video) moves one sample at a time; at
  1080p it costs more than the decode of the frame. Same bytes whatever the loop.
- **One thread for the audio analyses.** `AudioAnalysis.Run` feeds the loudness graph (ebur128 upsampling to 192 kHz
  for true peaks) and the conversion for the other analyses on the same thread, one after the other.
- **Measures.** GamutMeter rebuilds its lookup table for every meter (64 MB at 12 bits); FreezeDetector's sum of
  differences widens bytes three times and is scalar at 9-16 bits; TruePeakMeter's interpolation is scalar.
- **Smaller or riskier** (the backlog's Maybe rows): swscale's slice threads (faster scaling, but scale's slice
  threads varied ffmpeg's frames run to run on CI, Encoding.cs), pooled buffers for rotated and deinterlaced frames,
  the extra copy for Yuv420p output, ReadAhead's handoff, the wide picture meters, LoudnessMeter's K-weighting,
  AudioFilter's output nobody reads, the 8-bit meters' leftovers, small per-frame allocations, the start-code scans,
  the small audio loops, hardware decoding with frames in flight and beyond Windows, and an LTO native build.
- **No benchmarks** for any of it: frame-reader-bench.yml timed only reading frames against ffmpeg's pipe.

## The question

Which should be worked on (each its own pull request, with ffmpeg comparison tests and a backlog row)?

- Benchmarks first: rotated, scaled, ReadAt, audio-analysis and measure cases, so each later change can be measured
  (recommended)
- Vectorise rotation (FrameDecoder's Orient)
- The audio analysis on two threads (the loudness graph on its own)
- Quick measure fixes: GamutMeter's table cached, FreezeDetector's sum with psadbw, TruePeakMeter vectorised

## The answer

All four (the owner, 2026-11-07). The benchmarks went in one pull request; the other three each went to a cloud
session of its own, in parallel (they change different files), each measuring with the new benchmark once it was
on main. The rest stays in the backlog's Maybe section.
