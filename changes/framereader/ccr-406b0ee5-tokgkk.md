### Faster

- `VideoFrameReader` with `ReadAhead`: `TryRead` (without `FrameRate`) has the decoding thread convert the frames it reads
  ahead to `FrameFormat` too, so the caller's thread only hands them over; the same bytes, as each thread has its own
  swscale context with the same settings. The other reads convert in the read, as before. Each frame ahead then holds
  its converted copy as well as the decoded frame.
