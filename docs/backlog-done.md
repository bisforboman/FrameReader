# Backlog: done

Rows moved from [backlog.md](backlog.md) when Done, Dropped or Won't do, oldest first. Those from before 2026-11-07
are in [ShotDetector's backlog-done.md](https://github.com/bisforboman/shotdetector/blob/main/docs/backlog-done.md).

- Benchmarks for FrameReader's own paths: rotated, scaled and Yuv420p reads (ReadPaths), a picture a second (Sampling), the audio analyses (AudioAnalyses) and the managed measures (PictureMeasures, AudioMeasures), in frame-reader-bench.yml's weekly run
  - Why: the benchmark timed only reading frames against ffmpeg's pipe, so the performance work of 2026-11-07 couldn't be measured (docs/decisions/2026-11-07-performance-work.md)
  - Status: Done (2026-11-07)

- Faster measures: GamutMeter's table, FreezeDetector's SAD, TruePeakMeter's interpolation
  - Why: GamutMeter rebuilds its table for every meter (64 MB and most of a second at 12 bits); FreezeDetector's sum of differences widens bytes three times and is scalar above 8 bits (much slower at 10 bits than at 8); TruePeakMeter interpolates one point at a time. Integer sums, a cache, and vectors that keep each point's order: the same results (docs/decisions/2026-11-07-performance-work.md)
  - Status: Done (2026-11-07)
