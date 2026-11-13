# Coming from FFMpegCore

[FFMpegCore](https://github.com/rosenbjerg/FFMpegCore) builds ffmpeg and ffprobe command lines and runs them as
processes. FrameReader calls FFmpeg's libraries in your process: no process to start, no pipe to copy frames through,
no output to parse. For the jobs below the results are what ffmpeg's command line gives, byte for byte where the
format allows, and the tests compare them with it.

| Job | FFMpegCore | FrameReader |
|---|---|---|
| [Probe](#probe-duration-size-codecs-rotation) (duration, size, codecs, rotation) | `FFProbe.Analyse` | `MediaProbe.Probe` (`ProbeJson`: ffprobe's own JSON) |
| [A thumbnail at a time](#a-thumbnail-at-a-time) | `FFMpeg.Snapshot` | `VideoFrameReader.TryTakeAt` and `ImageEncoder` |
| [Frames into memory](#frames-into-memory) | `RawVideoPipeSink`, `StreamPipeSink` | `VideoFrameReader.TryRead` |
| [Stream copy](#stream-copy-and-trimming) | `FFMpegArguments ... CopyChannel()` | `Remux.Copy` |
| [Trim without re-encoding](#stream-copy-and-trimming) | `FFMpeg.SubVideo` | `Remux.Copy` with `Start` and `End` |
| [Input from a Stream](#input-from-a-stream) | `StreamPipeSource` | a `Stream` overload on each reader and on `MediaProbe.Probe` |

Timings for every row, on the same files on the same machines (GitHub's x64 and ARM runners, BenchmarkDotNet):
`FFMpegCoreJobs` (probe, thumbnail, stream copy) and `ReadFrames` (frames into memory, also against the ffmpeg
executable's stdout) in [benchmarks/FrameReader.Benchmarks](../benchmarks/FrameReader.Benchmarks), run weekly by
[frame-reader-bench.yml](https://github.com/bisforboman/FrameReader/actions/workflows/frame-reader-bench.yml): the
current numbers are in its latest run's summary. To run them on your own machine and files:

```bash
BENCH_VIDEOS="/path/a.mp4;/path/b.mkv" dotnet run -c Release --project benchmarks/FrameReader.Benchmarks -- --filter '*FFMpegCoreJobs*'
```

(`SHOTDETECTOR_FFMPEG_LIBS`: a folder of FFmpeg 8's shared libraries; `BENCH_FFMPEG`: the folder with ffmpeg and
ffprobe for FFMpegCore, else PATH.)

## What you install

| | FFMpegCore | FrameReader |
|---|---|---|
| Package | `FFMpegCore` | `FrameReader` |
| FFmpeg | the ffmpeg and ffprobe executables, on PATH or in `GlobalFFOptions.Configure(new FFOptions { BinaryFolder = ... })` | FFmpeg 8's shared libraries: `FrameReader.Native.<rid>` (win-x64, linux-x64, linux-arm64, linux-musl-x64, osx-arm64) or `FrameReader.Native` for all of them, found next to your app; or a "shared" FFmpeg 8 build or the system's (`apk add ffmpeg-libs`), with `FFmpegLibraries.Load(folder)` |
| Licence | MIT; whatever your ffmpeg build is | MIT; FrameReader.Native is FFmpeg's libraries, LGPL-2.1-or-later, loaded dynamically ([its README](../src/FrameReader.Native/README.md)) |

```bash
dotnet add package FrameReader
dotnet add package FrameReader.Native.linux-x64
```

FrameReader.Native is built for reading, probing, audio, images and stream copy: it has no video encoders but those
FrameReader's tests use (no x264). For `VideoWriter`, load an FFmpeg 8 build with x264 instead.

## Probe (duration, size, codecs, rotation)

```csharp
// FFMpegCore: runs ffprobe, parses its JSON
var analysis = FFProbe.Analyse("video.mp4");
var (duration, width, height, codec, rotation) = (analysis.Duration, analysis.PrimaryVideoStream!.Width,
    analysis.PrimaryVideoStream.Height, analysis.PrimaryVideoStream.CodecName, analysis.PrimaryVideoStream.Rotation);
```

```csharp
// FrameReader: reads the headers in-process, decodes nothing
var info = MediaProbe.Probe("video.mp4");
var (duration, width, height, codec, rotation) = (info.Duration, info.Video!.Width, info.Video.Height, info.Video.Codec, info.Video.Rotation);
```

| FFMpegCore (`IMediaAnalysis`) | FrameReader (`MediaInfo`) |
|---|---|
| `Duration` | `Duration` (null when the container doesn't say) |
| `Format.FormatName` | `Container` |
| `PrimaryVideoStream.Width`, `Height`, `CodecName`, `PixelFormat` | `Video.Width`, `Height`, `Codec`, `PixelFormat` |
| `PrimaryVideoStream.Rotation` | `Video.Rotation` (degrees, counterclockwise, as ffprobe's display matrix side data; null for none) |
| `PrimaryVideoStream.AvgFrameRate`, `FrameRate` | `Video.AverageFrameRate`, `FrameRate` (exact `Rational`s) |
| `PrimaryAudioStream.SampleRateHz`, `Channels`, `CodecName` | `Streams.First(s => s.Type == StreamType.Audio)`: `SampleRate`, `Channels`, `Codec` |
| `VideoStreams`, `AudioStreams`, `SubtitleStreams` | `Streams` (every stream, with its language, title, flags and bit rate) |

For the fields `IMediaAnalysis` doesn't model (profile, level, colour, disposition, tags, side data),
`MediaProbe.ProbeJson(path)` gives ffprobe's `-print_format json -show_format -show_streams` output itself, the same
keys and values, without the process. `ProbeOptions.PacketTimestamps` adds every video packet's timestamps, sizes and
positions (`ffprobe -show_packets`; a local MP4's from its sample tables, without reading its media data), and
`PacketKeyframes` their keyframe flags.

## A thumbnail at a time

```csharp
// FFMpegCore: an ffmpeg process per picture
FFMpeg.Snapshot("video.mp4", "thumb.jpg", new Size(320, 180), TimeSpan.FromSeconds(10));
```

```csharp
// FrameReader: the same JPEG, byte for byte (ffmpeg -ss 10 -i video.mp4 -vf scale=320:180 -frames:v 1 thumb.jpg)
using var reader = new VideoFrameReader("video.mp4");
using var jpeg = new ImageEncoder(new() { Format = ImageFormat.Jpg });
if (reader.TryTakeAt(TimeSpan.FromSeconds(10), out var frame))
    using (frame)
        File.WriteAllBytes("thumb.jpg", jpeg.Encode(frame, new ImageFilterOptions { Width = 320, Height = 180 }));
```

`Snapshot`'s `-s 320x180` and `-vf scale=320:180` are the same filter, so both write the same file with the same
FFmpeg build. The seek is ffmpeg's accurate `-ss`: the first frame at or after the time, decoded from the keyframe
before it. Phone video comes out upright, as ffmpeg's autorotate turns it.

- Keep the reader and the encoder for more pictures: each `TryTakeAt` seeks the open file, with no process or
  re-open. `MaxWidth = 640` instead of `Width` and `Height` keeps the aspect ratio (`scale='min(640,iw)':-2`).
- In memory: `Encode` returns the file's bytes; `Encode(frame, stream)` writes them to a `Stream` (an HTTP response).
  PNG and WebP are `ImageFormat.Png` and `ImageFormat.WebP`.
- Pixels instead of a file: `reader.TryReadAt(time, out var picture)` with `Width`, `Height` and `FrameFormat =
  FrameFormat.Rgba32` gives the frame's bytes, for an image library or a GPU texture, without System.Drawing.
- A picture every second, or every minute, of a long video: `TryTakeForwardTo` (one pass, jumping between keyframes
  where it pays) or `VideoFrameReader.ReadAt` (several readers at once), in [the package's README](../src/FrameReader/README.md).

## Frames into memory

```csharp
// FFMpegCore: ffmpeg writes raw frames to a pipe, read into a stream
var output = new MemoryStream();
FFMpegArguments.FromFileInput("video.mp4")
    .OutputToPipe(new StreamPipeSink(output), o => o.WithCustomArgument("-vf scale=160:90:flags=bicubic,format=gray").ForceFormat("rawvideo"))
    .ProcessSynchronously();
// then cut output's bytes into 160 x 90 frames yourself
```

```csharp
// FrameReader: one frame at a time, the same bytes, in a buffer it reuses
using var reader = new VideoFrameReader("video.mp4", new() { Width = 160, Height = 90, FrameFormat = FrameFormat.Gray8 });
while (reader.TryRead(out var frame))
{
    ReadOnlySpan<byte> pixels = frame.Data; // 160 x 90, rows packed; valid until the next TryRead
    // frame.Index, frame.Time (from the input's start, as ffmpeg gives it), frame.Pts
}
```

The pixels are `-vf scale=W:H:flags=bicubic,format=F -f rawvideo`'s, byte for byte. Nothing is allocated per frame
(copy with `frame.Data.ToArray()` to keep one), and there is no whole-video buffer to cut up. `FrameFormat` is
`Bgr24` (the default), `Rgb24`, `Bgra32`, `Rgba32`, `Gray8` or `Yuv420p`. Without `Width` and `Height` frames keep
their own size. `FrameRate = new(1, 1)` is `-vf fps=1`, `Seek(time)` is `-ss`, and `ReadAhead = 4` decodes on a
thread of the reader's own while your loop works on the frame it has.

## Stream copy and trimming

```csharp
// FFMpegCore
FFMpegArguments.FromFileInput("film.mkv").OutputToFile("film.mp4", true, o => o.CopyChannel()).ProcessSynchronously();
```

```csharp
// FrameReader: the file ffmpeg -i film.mkv -c copy film.mp4 writes
Remux.Copy("film.mkv", "film.mp4");
Remux.Copy("film.mkv", "sound.m4a", new() { Streams = StreamSelection.Audio });                // -map 0:a
Remux.Copy("film.mkv", "web.mp4", new() { MuxerOptions = new Dictionary<string, string> { ["movflags"] = "+faststart" } });
Remux.Copy("phone.mp4", "upright.mp4", new() { DisplayRotation = 90 });                         // -display_rotation 90
Remux.Copy("film.mkv", "clip.mkv", new() { Start = TimeSpan.FromSeconds(30), End = TimeSpan.FromSeconds(45) }); // -ss 30 -to 45
```

`Remux.CanCopy(input, output)` says beforehand whether the streams fit the container, and why not, from the muxer
itself. `CopyAsync` and `Progress` are there for long files, and an `.mpd` output writes MPEG-DASH.

`Start` and `End` trim without re-encoding, as `FFMpeg.SubVideo` does: the file `ffmpeg -ss S -to E -i IN -c copy OUT`
writes, so the cut starts at the keyframe at or before `Start` (exact only where every frame is one). Keep FFMpegCore
for anything else that is an ffmpeg command line: filters on video, concatenating, burning in subtitles, encoding with
any encoder.

## Input from a Stream

FFMpegCore's `StreamPipeSource` pipes the stream into ffmpeg, which reads it as a pipe, once: the headers have to come
first (mkv, webm, ts, a faststart mp4). FrameReader's readers take a `Stream` too (`new VideoFrameReader(stream)`,
`new AudioReader(stream)`, `new FrameDecoder(stream)`, `MediaProbe.Probe(stream)`). A stream that can seek (a
`FileStream`, a `MemoryStream`; from the release after 2.25.0) is read as ffmpeg reads a file, so any MP4 opens,
including one with its moov at the end, as phones and cameras write them. A stream that can't seek is read once, as a
pipe, as with FFMpegCore.

[examples/ThumbnailApi](../examples/ThumbnailApi) is an ASP.NET Core minimal API that does the most asked-for job
with it: an upload in, a JPEG thumbnail and the video's duration out, from memory, with no temporary file and no
process.

```csharp
app.MapPost("/thumbnail", async (HttpRequest request, CancellationToken cancellationToken) =>
{
    using var video = new MemoryStream();
    await request.Body.CopyToAsync(video, cancellationToken);
    video.Position = 0;
    var info = MediaProbe.Probe(video, new ProbeOptions { InputOptions = inputOptions });
    video.Position = 0;
    using var reader = new VideoFrameReader(video, new() { Decoder = new() { InputOptions = inputOptions } });
    // reader.TryTakeAt(info.Duration / 10, ...), ImageEncoder: the JPEG; info.Duration: the duration
});
```

An upload is untrusted input: the sample allows only the containers a video upload comes in and no protocol but the
stream itself (`format_whitelist`, `protocol_whitelist`), so a playlist can't make FFmpeg open URLs or files (the
package README's [Security](../src/FrameReader/README.md#security) section).
