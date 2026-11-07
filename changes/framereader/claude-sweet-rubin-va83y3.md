### New

- A `Stream` that can seek (a `FileStream`, a `MemoryStream`, such as an upload buffered in memory) is read as ffmpeg
  reads a file: seekable and with its size known, from where it is when opened. `VideoFrameReader`, `FrameDecoder`,
  `AudioReader` and `MediaProbe.Probe` then open an MP4 whose moov comes last (ffmpeg's default and most cameras'), which
  read once gave no frames, and their probes, frames, seeks and audio are the file's. A stream that can't seek is read
  once, as before, as ffmpeg reads a pipe.
