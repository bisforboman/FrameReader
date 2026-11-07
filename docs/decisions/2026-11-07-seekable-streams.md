# Streams that can seek are read as files (2026-11-07)

Issue #8 asked for an ASP.NET Core sample that turns an upload into a thumbnail and its duration from a stream, with
no temporary file. FrameReader read every `Stream` once and never seeked it, as ffmpeg reads a pipe, so its docs
asked for the headers first (mkv, webm, ts, mov, a faststart mp4). An MP4 with its moov after its mdat (ffmpeg's
default, and most phones' and cameras') didn't open. Worse, it opened without an error and gave no frames: the mov
demuxer finds the moov by reading past the mdat, which is then gone once it is past the 64 KB read buffer
(`cat moov-last.mp4 | ffmpeg -i -` fails the same way; a file small enough to fit in the buffer reads).
FFMpegCore's `StreamPipeSource` has the same limit, since it pipes into ffmpeg.

## Question

What should the sample do with an MP4 whose moov comes last?

## Choices

1. **Seekable Streams** (recommended): when `Stream.CanSeek`, `StreamInput` gives FFmpeg the file protocol's seek and
   size, offsets counted from the stream's position when opened. The sample buffers the upload in a `MemoryStream`, so
   any MP4 works. A new feature, with tests against the file path and a changelog fragment.
2. **Headers-first only**: the sample takes mkv, webm, ts, mov and faststart MP4, and answers 415 with the reason for
   anything else. Seekable Streams goes on the backlog.

## Answer

**Seekable Streams** (the owner, 2026-11-07). Settled in the code:

- A stream that can seek is read as `ffmpeg -i file` reads the file: `avio_alloc_context` gets the seek function, so
  the context is `AVIO_SEEKABLE_NORMAL` as the file protocol's is. `AVSEEK_SIZE` is the stream's length after the
  origin, or ENOSYS when the stream can't tell, as the file protocol does when fstat can't. `AVSEEK_FORCE` is ignored.
  Every `Stream` overload gets it: `VideoFrameReader`, `FrameDecoder`, `AudioReader` and `MediaProbe.Probe`.
- A stream that can't seek, or one after a prefix of bytes already read, is read once, as before.
- `SeekableStreamTests`: a moov-last MP4 from a FileStream, a MemoryStream and a MemoryStream after 1000 other bytes
  gives the path's probe (packet timestamps, sizes and positions included), frames, seeks, forward reads and audio;
  an MP3 gives the path's samples and duration. Read once, the moov-last MP4 gives fewer frames than the file.
- What is left: `FrameDecoder.CanReadAgain` still rules out every Stream (`_io == null`), so `TryReadForwardTo` from a
  seekable Stream leaves out only the frames it would leave out from a pipe, not the ones its guesses leave out on the
  path: the same frames, perhaps more of them decoded (not measured). A read-once Stream of a moov-last MP4 still
  gives no frames without an error, where ffmpeg fails. Both are backlog items. `FrameDecoder.cs` was left alone because an open pull request changes it.

The comparison page (docs/coming-from-ffmpegcore.md), the benchmark class and the sample went in as new files. The
links from the READMEs and the backlog rows follow in a docs PR once the open pull requests that change those files
have merged (the owner, 2026-11-07).
