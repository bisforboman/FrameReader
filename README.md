# FrameReader

Fast in-process video and audio for .NET through FFmpeg's libraries: frames at a size and pixel format with the bytes
ffmpeg's scale and format filters give, exact seeking, thumbnails, audio samples and measures, writing, stream copy
and probing. No ffmpeg process, no pipes.

```bash
dotnet add package FrameReader
dotnet add package FrameReader.Native.linux-x64   # FFmpeg's libraries for your platform (or FrameReader.Native: all)
```

- What it does and how to use it: [src/FrameReader/README.md](src/FrameReader/README.md), the package's front page.
- Releases: [src/FrameReader/CHANGELOG.md](src/FrameReader/CHANGELOG.md).
- Our FFmpeg build: [src/FrameReader.Native/README.md](src/FrameReader.Native/README.md).
- Coming from FFMpegCore: [docs/coming-from-ffmpegcore.md](docs/coming-from-ffmpegcore.md), and an ASP.NET Core
  thumbnail API in [examples/ThumbnailApi](examples/ThumbnailApi).
- Splitting a digitised tape at its blank stretches (black, a blue screen, a frozen picture, silence), a report of
  where to cut: [examples/TapeSplitter](examples/TapeSplitter).

FrameReader began as [ShotDetector](https://github.com/bisforboman/shotdetector)'s in-process decoding and lived in
its repository until 2026-11-07; its history came with it. MIT ([LICENSE](LICENSE)); FFmpeg's libraries, in the
FrameReader.Native packages, are LGPL-2.1-or-later and loaded dynamically; what they link in:
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
